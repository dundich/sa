using System.Security;
using Microsoft.Extensions.Logging;

namespace Sa.Data.TempFolder;

/// <summary>
/// Measures the total volume of a temp-folder root on a low-priority background thread, caches
/// per-subfolder sizes keyed by the subfolder's last write time, and raises the
/// <see cref="TempFolderOptions.OnVolumeExceeded"/> event edge-triggered when the measured total
/// crosses <see cref="TempFolderOptions.MaxTotalSize"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Low priority.</b> The walk runs on a dedicated background thread at
/// <see cref="ThreadPriority.BelowNormal"/> (a reduced nice value on Unix), so summing a large
/// tree never preempts request handling. A cancellation check runs inside the walk.
/// </para>
/// <para>
/// <b>Cache.</b> A subfolder whose last write time is unchanged since the previous scan reuses its
/// cached size — only touched or modified folders are re-walked. Entries for folders that vanished
/// are dropped, and deletions performed through the instance additionally invalidate the affected
/// top-level entry right away (see <see cref="Invalidate"/>), since a deep delete alone never
/// moves that folder's stamp. Nothing is ever measured unless <see cref="TempFolderOptions.TrackVolume"/> is on
/// and a positive <see cref="TempFolderOptions.MaxTotalSize"/> is configured.
/// </para>
/// <para>
/// <b>Symlinks.</b> Symbolic links are neither followed nor counted — at the top level or nested.
/// Their content belongs to the link's target (which may even cycle back), so following one would
/// both inflate the total with someone else's bytes and risk a walk that never ends.
/// </para>
/// <para>
/// <b>Edge-triggered event.</b> The delegate fires once on the transition into "over the limit"
/// and once on the transition back — never repeatedly while the state holds. Handler exceptions
/// are caught and logged; they never break the scan.
/// </para>
/// </remarks>
internal sealed class VolumeScanner
{
    private const int CancellationCheckInterval = 256;

    /// <summary>Cache key for the loose files lying directly in the root.</summary>
    private const string RootFilesCacheKey = " root";

    private readonly string _name;
    private readonly string _rootPath;
    private readonly ILogger _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CachedFolder> _cache = new(StringComparer.Ordinal);
    private bool? _exceeded;

    public VolumeScanner(string name, string rootPath, ILogger logger)
    {
        _name = name;
        _rootPath = rootPath;
        _logger = logger;
    }

    /// <summary>
    /// Runs one measurement pass on a low-priority thread and evaluates the volume event.
    /// Returns the measured total in bytes.
    /// </summary>
    public Task<long> ScanAsync(TempFolderOptions options, CancellationToken cancellationToken)
        => RunLowPriorityAsync(() => ScanCore(options, cancellationToken), cancellationToken);

    private long ScanCore(TempFolderOptions options, CancellationToken cancellationToken)
    {
        var total = 0L;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (Directory.Exists(_rootPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(_rootPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (PathGuard.IsSymlink(dir))
                {
                    // Not our tree: the folder is a link and its content lives elsewhere — never
                    // measured (nor cached), so a planted link cannot inflate the total and a
                    // link cycle cannot spin the walk.
                    continue;
                }

                seen.Add(dir);
                total += MeasureFolder(dir, cancellationToken);
            }

            // Loose files lying directly in the root: counted (volume we do not manage but still
            // pay for), cached under the root's own stamp.
            total += MeasureRootFiles(cancellationToken);
        }

        PruneCache(seen);

        EvaluateTransition(options, total);
        return total;
    }

    /// <summary>Walks one top-level subfolder, reusing the cache while its stamp is unchanged.</summary>
    private long MeasureFolder(string dir, CancellationToken cancellationToken)
    {
        DateTimeOffset stamp;
        try
        {
            stamp = Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(dir, out var cached) && cached.Stamp == stamp)
            {
                return cached.Size;
            }
        }

        long size;
        try
        {
            size = WalkTree(dir, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The walk failed mid-tree: report zero for this pass and leave the cache unwritten,
            // so the next scan retries instead of pinning a partial sum.
            _logger.LogDebug(ex, "Temp folder '{Name}': partial walk of {Dir} discarded.", _name, dir);
            return 0;
        }

        // Only cache a completed walk: a cancelled pass must be redone in full.
        if (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _cache[dir] = new CachedFolder(stamp, size);
            }
        }

        return size;
    }

    private long MeasureRootFiles(CancellationToken cancellationToken)
    {
        var rootStamp = Directory.GetLastWriteTimeUtc(_rootPath);

        lock (_gate)
        {
            if (_cache.TryGetValue(RootFilesCacheKey, out var cached) && cached.Stamp == rootStamp)
            {
                return cached.Size;
            }
        }

        var size = 0L;
        var index = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_rootPath, "*", TopLevelFiles))
            {
                if (++index % CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                try
                {
                    size += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Raced with a writer/deleter — the next scan sees the new state.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return size;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                _cache[RootFilesCacheKey] = new CachedFolder(rootStamp, size);
            }
        }

        return size;
    }

    private static long WalkTree(string dir, CancellationToken cancellationToken)
    {
        var total = 0L;
        var index = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*", AllFiles))
        {
            if (++index % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Raced with a writer/deleter — the next scan sees the new state.
            }
        }

        return total;
    }

    /// <summary>
    /// Drops the cached size of the top-level subfolder <paramref name="absolutePath"/> belongs to,
    /// so the next scan re-walks it. Called after a successful deletion: deleting deep inside a
    /// hierarchy does not move the top-level folder's last write time — the cache is keyed by
    /// exactly that stamp — and would otherwise keep serving the pre-delete total until some later
    /// write happens to touch the ancestor chain. A path directly in the root needs no help: the
    /// root's own stamp moves with it.
    /// </summary>
    /// <exception cref="SecurityException">The path is outside the root — nothing to invalidate.</exception>
    public void Invalidate(string absolutePath)
    {
        var top = PathGuard.TopSegment(_rootPath, absolutePath);
        if (top is null)
        {
            return; // sits directly in the root — its stamp already moved
        }

        // Same construction as Directory.EnumerateDirectories(_rootPath) uses for its results,
        // so the key matches the cache textually.
        var key = Path.Combine(_rootPath, top);

        lock (_gate)
        {
            _cache.Remove(key);
        }
    }

    private void PruneCache(HashSet<string> seenFolders)
    {
        lock (_gate)
        {
            var stale = _cache.Keys
                .Where(key => key != RootFilesCacheKey && !seenFolders.Contains(key))
                .ToList();

            foreach (var key in stale)
            {
                _cache.Remove(key);
            }
        }
    }

    /// <summary>Fires <see cref="TempFolderOptions.OnVolumeExceeded"/> on state transitions only.</summary>
    private void EvaluateTransition(TempFolderOptions options, long total)
    {
        bool transitionedTo;
        lock (_gate)
        {
            if (options.MaxTotalSize <= 0)
            {
                _exceeded = null;
                return;
            }

            var exceeded = total > options.MaxTotalSize;

            if (_exceeded is null && !exceeded)
            {
                // First reading and already within the limit: record the state silently — there is
                // no "recovery" to announce (FreeSpaceMonitor does the same). A first reading *over*
                // the limit falls through: that is a real entry into the state and does fire.
                _exceeded = false;
                return;
            }

            if (_exceeded == exceeded)
            {
                return; // state holds — edge-triggered, no repeat
            }

            _exceeded = exceeded;
            transitionedTo = exceeded;
        }

        var args = new TempFolderVolumeArgs(_name, _rootPath, total, options.MaxTotalSize, transitionedTo);

        if (transitionedTo)
        {
            _logger.LogWarning(
                "Temp folder '{Name}' volume {Total} bytes exceeds the limit {Max} bytes (root {Root}).",
                _name, total, options.MaxTotalSize, _rootPath);
        }
        else
        {
            _logger.LogInformation(
                "Temp folder '{Name}' volume {Total} bytes is back under the limit {Max} bytes (root {Root}).",
                _name, total, options.MaxTotalSize, _rootPath);
        }

        var handler = options.OnVolumeExceeded;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(args);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Temp folder '{Name}' OnVolumeExceeded handler threw.", _name);
        }
    }

    /// <summary>
    /// Runs <paramref name="func"/> on a dedicated background thread with a below-normal thread
    /// priority, so a full-tree walk never preempts normal request work.
    /// </summary>
    private static Task<T> RunLowPriorityAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or SecurityException)
                {
                    // Priority is a best-effort nicety; the scan must still run without it.
                }

                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "sa-tempfolder-volume-scan",
        };

        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Every file, including hidden and system ones: <see cref="EnumerationOptions"/> skips
    /// <c>Hidden|System</c> by default, which would silently under-count the volume. Reparse
    /// points are skipped explicitly: following a linked directory would count — and recurse
    /// into — content outside the root, and a link cycle would never finish.
    /// </summary>
    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>
    /// Loose files directly in the root: hidden and system ones count, links do not (their
    /// length is the target's, which is not this instance's volume).
    /// </summary>
    private static readonly EnumerationOptions TopLevelFiles = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private readonly record struct CachedFolder(DateTimeOffset Stamp, long Size);
}
