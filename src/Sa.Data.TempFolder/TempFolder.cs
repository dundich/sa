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
    public string CreateSubfolder(string? relativeSubfolder = null)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(CreateSubfolder));

        string full;
        if (string.IsNullOrWhiteSpace(relativeSubfolder))
        {
            // The strategy's output goes through the guard too: a hostile or buggy strategy
            // cannot hand out a name that escapes the root.
            full = PathGuard.Resolve(RootPath, _namingStrategy.CreateFolderName(_options));
        }
        else
        {
            full = PathGuard.Resolve(RootPath, relativeSubfolder);
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
    public async ValueTask<(string RelativePath, string AbsolutePath)> SaveStreamAsync(
        Stream source, string relativePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfReadOnly(nameof(SaveStreamAsync));
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A file path relative to the root is required.", nameof(relativePath));
        }

        // The whole write is one activity: no cleanup pass can run between the parent creation
        // and the last byte (a running one is cancelled by this very entry).
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        return await WriteAsync(source, relativePath, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<(string RelativePath, string AbsolutePath)> CopyFileAsync(
        string sourcePath, string? relativeSubfolder = null, CancellationToken cancellationToken = default)
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
        // against the cleanup pass (see SaveStreamAsync).
        using var lease = await _cleanupGate.EnterActivityAsync(cancellationToken).ConfigureAwait(false);

        // No subfolder given → build one from the naming strategy, per spec.
        var directory = string.IsNullOrWhiteSpace(relativeSubfolder)
            ? CreateSubfolder()
            : PathGuard.Resolve(RootPath, relativeSubfolder);
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, fileName);
        if (!PathGuard.IsInside(RootPath, destination))
        {
            throw new SecurityException(
                $"Copying '{sourcePath}' would land outside the temp root '{RootPath}': '{destination}'.");
        }

        if (!_options.OverwriteFiles && File.Exists(destination))
        {
            // Same policy as SaveStreamAsync, checked before the source is even opened.
            throw new IOException(
                $"File '{destination}' already exists and OverwriteFiles is false.");
        }

        var relative = Path.GetRelativePath(RootPath, destination);

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await WriteAsync(source, relative, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> EnumerateFilesAsync(
        string pattern,
        string? relativeSubfolder = null,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("A file pattern is required.", nameof(pattern));
        }

        var directory = string.IsNullOrWhiteSpace(relativeSubfolder)
            ? RootPath
            : PathGuard.Resolve(RootPath, relativeSubfolder);

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
    public async ValueTask<int> CleanupAsync(CancellationToken cancellationToken = default)
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

        // Deletion yields to activity: the pass does not start while a write is in flight
        // (TryEnterCleanup → null → this pass is cancelled, the next interval retries), and a
        // write arriving mid-pass cancels pass.Token — the deletion then ends by cancellation
        // at its next checkpoint instead of racing the operation.
        using var pass = _cleanupGate.TryEnterCleanup(cancellationToken);

        if (pass is null)
        {
            _logger.LogDebug(
                "Temp folder '{Name}': cleanup pass cancelled — a write or a marker walk is in progress.",
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
            // An arriving write interrupted the pass — the expected outcome, not an error.
            _logger.LogInformation(
                "Temp folder '{Name}': cleanup pass cancelled by concurrent write activity " +
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
    public ValueTask CheckAccessAsync(CancellationToken cancellationToken = default)
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

    private async ValueTask<(string RelativePath, string AbsolutePath)> WriteAsync(
        Stream source, string relativePath, CancellationToken cancellationToken)
    {
        var full = PathGuard.Resolve(RootPath, relativePath);

        if (Directory.Exists(full))
        {
            throw new InvalidOperationException(
                $"Path '{relativePath}' points at a directory ('{full}'); a file path is required.");
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

        await using (var target = new FileStream(
            full,
            _options.OverwriteFiles ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        // Debounced activation of the written file's own directory and every ancestor up to the
        // root: nested cleanup ages those levels too, and overwrites alone never move a
        // directory's mtime. A file lying directly in the root has no aging parent — no request.
        if (PathGuard.TopSegment(RootPath, full) is not null)
        {
            _debouncer.Request(Path.GetDirectoryName(full)!);
        }

        return (Path.GetRelativePath(RootPath, full), full);
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
