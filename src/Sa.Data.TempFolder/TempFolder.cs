using System.Runtime.CompilerServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Sa.Classes;
using Sa.Data.TempFolder.Cleanup;
using Sa.Data.TempFolder.Naming;

namespace Sa.Data.TempFolder;

/// <summary>
/// Default <see cref="ITempFolder"/>: path-guarded file operations under one root, debounced
/// activity marking, age-based cleanup with retries (yielding to in-flight writes via
/// <see cref="CleanupGate"/>), and an optional low-priority volume scan.
/// One instance per registration; constructed by <see cref="Setup.AddSaTempFolder"/>'s keyed
/// factory from the registration's options snapshot and strategies.
/// </summary>
internal sealed class TempFolder : ITempFolder
{
    private const int CopyBufferSize = 81920;

    private readonly TempFolderOptions _options;
    private readonly ICleanupStrategy _cleanupStrategy;
    private readonly IFolderNameStrategy _namingStrategy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CleanupGate _cleanupGate = new();
    private readonly TouchDebouncer _debouncer;
    private readonly VolumeScanner _volumeScanner;
    private readonly FreeSpaceMonitor _freeSpaceMonitor;
    private bool _disposed;

    public TempFolder(
        string name,
        TempFolderOptions options,
        ICleanupStrategy cleanupStrategy,
        IFolderNameStrategy namingStrategy,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        Name = name;
        _options = options;
        _cleanupStrategy = cleanupStrategy;
        _namingStrategy = namingStrategy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        RootPath = Path.GetFullPath(options.RootPath);

        _debouncer = new TouchDebouncer(_cleanupGate, RootPath, options.TouchDebounce, _timeProvider);
        _volumeScanner = new VolumeScanner(name, RootPath, logger);
        _freeSpaceMonitor = new FreeSpaceMonitor(name, RootPath, logger);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string RootPath { get; }

    /// <inheritdoc />
    public string CreateSubfolder(string? subfolder = null)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(CreateSubfolder));

        string full;
        if (string.IsNullOrWhiteSpace(subfolder))
        {
            // The strategy's output goes through the guard too: a hostile or buggy strategy
            // cannot hand out a name that escapes the root.
            full = PathGuard.Resolve(RootPath, _namingStrategy.CreateFolderName(_options));
        }
        else
        {
            full = PathGuard.Resolve(RootPath, subfolder);
        }

        // Shared activity side: a concurrent cleanup pass can neither start while this creation
        // runs nor survive one already under way, so the returned folder cannot be taken back
        // mid-call. The wait only bites when a pass is active (and cancels it); otherwise the
        // lease is already complete and this does not block.
        using var lease = _cleanupGate
            .EnterActivityAsync(CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Directory.CreateDirectory(full);
        return full;
    }

    /// <inheritdoc />
    public async ValueTask<(string RelativePath, string AbsolutePath)> WriteAsync(
        Stream source, string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(WriteAsync));
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path relative to the root is required.", nameof(path));
        }

        // The whole write is one activity: no cleanup pass can run between the parent creation
        // and the last byte (a running one is cancelled by this very entry).
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        return await WriteCoreAsync(source, path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(string RelativePath, string AbsolutePath)> CopyFileAsync(
        string sourcePath, string? subfolder = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(CopyFileAsync));
        ArgumentNullException.ThrowIfNull(sourcePath);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Source file not found: '{sourcePath}'.", sourcePath);
        }

        var fileName = Path.GetFileName(sourcePath);
        if (fileName is "" or "." or "..")
        {
            throw new ArgumentException($"'{sourcePath}' does not name a file.", nameof(sourcePath));
        }

        // The source file may live anywhere on disk, but its *name* is about to become a name
        // inside the temp folder — reject injection-style characters before creating anything.
        if (PathGuard.HasUnsafeCharacters(fileName))
        {
            throw new SecurityException(
                $"Source file name '{fileName}' contains characters not allowed inside the temp folder.");
        }

        // Source file name validated; from here on everything — directory creation, the
        // overwrite policy check, opening the source and the copy itself — is one activity
        // against the cleanup pass (see WriteAsync).
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        // No subfolder given → build one from the naming strategy, per spec.
        var directory = string.IsNullOrWhiteSpace(subfolder)
            ? CreateSubfolder()
            : PathGuard.Resolve(RootPath, subfolder);
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, fileName);
        if (!PathGuard.IsInside(RootPath, destination))
        {
            throw new SecurityException(
                $"Copying '{sourcePath}' would land outside the temp root '{RootPath}': '{destination}'.");
        }

        if (!_options.OverwriteFiles && File.Exists(destination))
        {
            // Same policy as WriteAsync, checked before the source is even opened.
            throw new IOException(
                $"File '{destination}' already exists and OverwriteFiles is false.");
        }

        var relative = Path.GetRelativePath(RootPath, destination);

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await WriteCoreAsync(source, relative, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ReadAsync(
        string path,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(loadStream);

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path relative to the root is required.", nameof(path));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Validated before the gate: a hostile or wrong path must not cancel a running pass.
        var full = PathGuard.Resolve(RootPath, path);

        if (Directory.Exists(full))
        {
            throw new InvalidOperationException(
                $"Path '{path}' points at a directory ('{full}'); a file path is required.");
        }

        // The whole read is one activity: no cleanup pass can run between the existence check
        // and the end of the callback (a running one is cancelled by this very entry), so the
        // file is never deleted under the open stream.
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(full))
        {
            return false;
        }

        try
        {
            await using var stream = new FileStream(
                full, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await loadStream(stream, cancellationToken).ConfigureAwait(false);

            // A read counts as activity too: without this a folder that is only ever read from
            // would age out exactly like an unused one.
            RequestActivity(full);

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted out-of-band between the check and the open — a vanished file reads as
            // "not found" instead of failing the caller.
            _logger.LogDebug(ex, "Temp folder '{Name}': read of '{Path}' found no file.", Name, full);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(DeleteFileAsync));

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A file path relative to the root is required.", nameof(path));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Validated before the gate: a hostile or wrong path must not cancel a running pass.
        var full = PathGuard.Resolve(RootPath, path);

        if (Directory.Exists(full))
        {
            throw new InvalidOperationException(
                $"Path '{path}' points at a directory ('{full}'); a file path is required.");
        }

        // The whole delete is one activity: no cleanup pass can run between the existence check
        // and the last attempt (a running one is cancelled by this very entry), so the pass can
        // neither take the folder's contents mid-delete nor be taken by surprise.
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        if (!File.Exists(full))
        {
            return false;
        }

        try
        {
            await Retry.Linear(
                static (string target, CancellationToken token) =>
                {
                    // Re-check per attempt: a concurrent cleaner or caller may have removed it
                    // already — and File.Delete is a no-op on a missing file, so a vanish under
                    // the attempt reads as success, not as a retryable failure.
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    return ValueTask.FromResult(true);
                },
                full,
                retryCount: 3,
                initialDelay: 100,
                shouldRetry: static (ex, _) => ex is IOException or UnauthorizedAccessException,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The file (or its directory) vanished out-of-band between the check and the delete
            // — a vanished file reads as "not deleted" instead of failing the caller.
            _logger.LogDebug(ex, "Temp folder '{Name}': delete of '{Path}' found no file.", Name, full);
            return false;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> EnumerateFilesAsync(
        string pattern,
        string? subfolder = null,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("A file pattern is required.", nameof(pattern));
        }

        var directory = string.IsNullOrWhiteSpace(subfolder)
            ? RootPath
            : PathGuard.Resolve(RootPath, subfolder);

        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        foreach (var file in Directory.EnumerateFiles(directory, pattern, enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_options.ReadOnly)
        {
            _logger.LogDebug("Temp folder '{Name}' is read-only; cleanup pass ignored.", Name);
            return 0;
        }

        if (ShouldTrackVolume())
        {
            try
            {
                await _volumeScanner.ScanAsync(_options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Age-based cleanup does not depend on the measurement — never lose the pass to it.
                _logger.LogWarning(ex, "Temp folder '{Name}': volume scan before cleanup failed.", Name);
            }
        }

        // Deletion yields to activity: the pass does not start while a read or write is in
        // flight (TryEnterCleanup → null → this pass is cancelled, the next interval retries),
        // and an arriving operation cancels pass.Token — the deletion then ends by cancellation
        // at its next checkpoint instead of racing the operation.
        using var pass = _cleanupGate.TryEnterCleanup(cancellationToken);

        if (pass is null)
        {
            _logger.LogDebug(
                "Temp folder '{Name}': cleanup pass cancelled — file activity or a marker walk is in progress.",
                Name);
            return 0;
        }

        var deleted = 0;
        var skipped = 0;
        IReadOnlyList<string> candidates = [];

        try
        {
            try
            {
                candidates = _cleanupStrategy.SelectForDeletion(new CleanupContext
                {
                    RootPath = RootPath,
                    Options = _options,
                    TimeProvider = _timeProvider,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Temp folder '{Name}': cleanup strategy failed; pass skipped.", Name);
                return 0;
            }

            foreach (var path in candidates)
            {
                // Caller cancellation and an arriving write both land here: the pass stops
                // before its next deletion rather than racing the operation.
                pass.Token.ThrowIfCancellationRequested();

                if (!PathGuard.IsInside(RootPath, path))
                {
                    _logger.LogWarning(
                        "Temp folder '{Name}': cleanup strategy returned a path outside the root; skipped: {Path}",
                        Name, path);
                    continue;
                }

                if (_debouncer.HasPendingWithin(path))
                {
                    // The subtree was just written to and its debounced activity markers have
                    // not landed yet — deleting now would take the fresh file with it. The next
                    // pass sees the refreshed markers (or retries once they land).
                    skipped++;
                    continue;
                }

                if (await TryDeleteAsync(path, pass.Token).ConfigureAwait(false))
                {
                    deleted++;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An arriving read or write interrupted the pass — the expected outcome, not an error.
            _logger.LogInformation(
                "Temp folder '{Name}': cleanup pass cancelled by concurrent file activity " +
                "({Deleted} deleted, {Skipped} skipped).",
                Name, deleted, skipped);
            return deleted;
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "Temp folder '{Name}': cleanup pass deleted {Deleted} of {Candidates} candidate folders " +
                "under {Root} and skipped {Skipped} just-written ones.",
                Name, deleted, candidates.Count, RootPath, skipped);
        }
        else if (deleted > 0)
        {
            _logger.LogInformation(
                "Temp folder '{Name}': cleanup pass deleted {Deleted} of {Candidates} candidate folders under {Root}.",
                Name, deleted, candidates.Count, RootPath);
        }
        else
        {
            _logger.LogDebug(
                "Temp folder '{Name}': cleanup pass deleted nothing ({Candidates} candidates under {Root}).",
                Name, candidates.Count, RootPath);
        }

        return deleted;
    }

    /// <inheritdoc />
    public ValueTask EnsureAccessAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(RootPath))
        {
            if (_options.ReadOnly)
            {
                throw new InvalidOperationException(
                    $"Temp folder '{Name}' root '{RootPath}' does not exist and cannot be created: the instance is read-only.");
            }

            try
            {
                Directory.CreateDirectory(RootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Temp folder '{Name}': cannot create root '{RootPath}'.", ex);
            }
        }

        // Read check (both modes): the root must be listable.
        try
        {
            foreach (var _ in Directory.EnumerateFileSystemEntries(RootPath))
            {
                break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Temp folder '{Name}': root '{RootPath}' cannot be listed (read access failed).", ex);
        }

        if (_options.ReadOnly)
        {
            // Read-only: no write / create / delete probes — the instance must not mutate anything.
            _logger.LogDebug("Temp folder '{Name}' read-only root '{Root}' verified readable.", Name, RootPath);
            return ValueTask.CompletedTask;
        }

        // Write / create / delete probe: a throwaway file and folder, removed on the way out.
        var probeFolder = Path.Combine(RootPath, $".sa_probe_{Guid.CreateVersion7():N}");
        var probeFile = Path.Combine(probeFolder, "probe.tmp");

        try
        {
            Directory.CreateDirectory(probeFolder);

            using (var stream = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probeFile);
            Directory.Delete(probeFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a failed probe must not leave debris behind.
            try
            {
                if (File.Exists(probeFile)) File.Delete(probeFile);
                if (Directory.Exists(probeFolder)) Directory.Delete(probeFolder);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(cleanupEx, "Temp folder '{Name}': probe cleanup failed.", Name);
            }

            throw new InvalidOperationException(
                $"Temp folder '{Name}': root '{RootPath}' failed the write/create/delete access probe.", ex);
        }

        _logger.LogDebug("Temp folder '{Name}' root '{Root}' verified readable and writable.", Name, RootPath);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs the low-priority volume measurement for this instance — used by the background service
    /// for the independent <see cref="TempFolderOptions.VolumeScanInterval"/> loop.
    /// </summary>
    internal Task<long> ScanVolumeAsync(CancellationToken cancellationToken)
        => _volumeScanner.ScanAsync(_options, cancellationToken);

    /// <summary>Whether a volume measurement is configured to run at all.</summary>
    internal bool ShouldTrackVolume()
        => _options.TrackVolume && _options.MaxTotalSize > 0;

    /// <summary>Whether the background service should run the independent volume-scan loop.</summary>
    internal bool HasPeriodicVolumeScan()
        => ShouldTrackVolume() && _options.VolumeScanInterval > TimeSpan.Zero;

    /// <summary>
    /// Runs one free-space check for this instance — used by the background service for the
    /// <see cref="TempFolderOptions.FreeSpaceCheckInterval"/> loop.
    /// </summary>
    internal long CheckFreeSpace()
        => _freeSpaceMonitor.Check(_options);

    /// <summary>Whether the background service should run the free-space check loop at all.</summary>
    internal bool HasFreeSpaceCheck()
        => _options.MinFreeSpace > 0;

    /// <summary>Options snapshot this instance was constructed with (reloads do not rebind it).</summary>
    internal TempFolderOptions OptionsSnapshot => _options;

    private async ValueTask<(string RelativePath, string AbsolutePath)> WriteCoreAsync(
        Stream source, string path, CancellationToken cancellationToken)
    {
        var full = PathGuard.Resolve(RootPath, path);

        if (Directory.Exists(full))
        {
            throw new InvalidOperationException(
                $"Path '{path}' points at a directory ('{full}'); a file path is required.");
        }

        if (!_options.OverwriteFiles && File.Exists(full))
        {
            throw new IOException(
                $"File '{full}' already exists and OverwriteFiles is false.");
        }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Smart preallocation: an already-sized source reserves its length up front so the file
        // never grows in chunks while the copy runs; a stream of unknown length writes through
        // the normal buffered path (the copy is going to fill it anyway).
        var targetOptions = new FileStreamOptions
        {
            Mode = _options.OverwriteFiles ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = CopyBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        if (source.CanSeek)
        {
            // The copy writes from the current position to the end — reserve exactly what will
            // land, or a partially-advanced source would leave zero padding at the tail.
            var remaining = source.Length - source.Position;
            if (remaining > 0 && remaining <= int.MaxValue)
            {
                targetOptions.PreallocationSize = (int)remaining;
            }
        }

        await using (var target = new FileStream(full, targetOptions))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // Debounced activation of the file's own directory and every ancestor up to the root:
        // nested cleanup ages those levels too, and overwrites alone never move a directory's
        // mtime. A file lying directly in the root has no aging parent — no request.
        RequestActivity(full);

        return (Path.GetRelativePath(RootPath, full), full);
    }

    /// <summary>
    /// Schedules the debounced refresh of the activity markers of <paramref name="absoluteFilePath"/>'s
    /// directory chain — the file's own directory and every ancestor up to (excluding) the root.
    /// A file lying directly in the root has no aging parent, so nothing is scheduled. Shared by
    /// writes (<see cref="WriteCoreAsync"/>) and reads (<see cref="ReadAsync"/>).
    /// </summary>
    private void RequestActivity(string absoluteFilePath)
    {
        if (PathGuard.TopSegment(RootPath, absoluteFilePath) is not null)
        {
            _debouncer.Request(Path.GetDirectoryName(absoluteFilePath)!);
        }
    }

    private async ValueTask<bool> TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await Retry.Linear(
                static (string target, CancellationToken token) =>
                {
                    // Re-check per attempt: a concurrent cleaner may have removed it already,
                    // which is success, not a retryable failure.
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                    }

                    return ValueTask.FromResult(true);
                },
                path,
                retryCount: 3,
                initialDelay: 100,
                shouldRetry: static (ex, _) => ex is IOException or UnauthorizedAccessException,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Temp folder '{Name}': failed to delete '{Path}' after retries.", Name, path);
            return false;
        }
    }

    private void ThrowIfReadOnly(string operation)
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Temp folder '{Name}' is read-only; {operation} is not allowed.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debouncer.Dispose();
    }
}
