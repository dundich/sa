namespace Sa.Data.TempFolder;

/// <summary>
/// Coalesces a burst of file accesses — writes and successful reads — into a single delayed
/// refresh of the activity markers of an accessed file's folder <b>and every ancestor up to the
/// root</b>: every <see cref="Request"/> re-arms one timer per folder, and only a quiet period of
/// <c>TouchDebounce</c> length actually touches the last write times.
/// </summary>
/// <remarks>
/// One entry per folder — the directory holding the accessed files; the timer is recreated on every
/// request and carries its own generation, so a callback already in flight for a superseded
/// request recognises itself and does nothing. The touch runs outside the lock — file-system calls
/// have no business serialising the dictionary — and walks upward one ancestor at a time, so both
/// the file's own directory (overwrites alone never move a directory's mtime) and the intermediate
/// levels of a date hierarchy stay fresh while the structure is in use. The instance root itself is
/// never touched. Pending touches are dropped on <see cref="Dispose"/>: the folders simply stay
/// older and are picked up by the next cleanup pass, which is the safe direction.
/// <para>
/// The walk itself rides the instance's <see cref="CleanupGate"/> activity side, and the pending
/// entry is only cleared <b>after</b> the walk has landed. A cleanup pass therefore sees either a
/// refreshed marker (the folder is not expired) or a still-pending entry (the folder is skipped —
/// see <see cref="HasPendingWithin"/>): a just-accessed file is never deleted in the quiet window
/// before its touch fires.
/// </para>
/// <para>
/// Built on <see cref="TimeProvider.CreateTimer"/> so tests advance the debounce with a manual
/// clock instead of sleeping.
/// </para>
/// </remarks>
internal sealed class TouchDebouncer : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly CleanupGate _cleanupGate;
    private readonly string _rootPath;
    private readonly TimeSpan _delay;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public TouchDebouncer(
        CleanupGate cleanupGate, string rootPath, TimeSpan delay, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cleanupGate);

        _cleanupGate = cleanupGate;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Trimmed, so the upward walk recognises its stop even when the configured root kept a
        // trailing separator.
        var root = Path.GetFullPath(rootPath);
        _rootPath = root.Length > 1
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : root;

        _delay = delay;
    }

    /// <summary>
    /// Schedules (or re-arms) the debounced touch of <paramref name="absoluteFolderPath"/> — a
    /// directory under the instance root that received a write or a successful read (never the root
    /// itself). A zero delay touches immediately. When the quiet period elapses, the folder and
    /// every ancestor up to (excluding) the root get their last write time refreshed.
    /// </summary>
    public void Request(string absoluteFolderPath)
    {
        ArgumentNullException.ThrowIfNull(absoluteFolderPath);

        var touchNow = false;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_entries.TryGetValue(absoluteFolderPath, out var entry))
            {
                entry = new Entry();
                _entries[absoluteFolderPath] = entry;
            }

            entry.Generation++;

            if (_delay == TimeSpan.Zero)
            {
                entry.Timer?.Dispose();
                _entries.Remove(absoluteFolderPath);
                touchNow = true;
            }
            else
            {
                var generation = entry.Generation;
                var timer = _timeProvider.CreateTimer(
                    static state =>
                    {
                        var (owner, path, entry, generation) = ((TouchDebouncer, string, Entry, long))state!;
                        owner.Fire(path, entry, generation);
                    },
                    (this, absoluteFolderPath, entry, generation),
                    _delay,
                    Timeout.InfiniteTimeSpan);

                entry.Timer?.Dispose();
                entry.Timer = timer;
            }
        }

        if (touchNow)
        {
            // Outside the lock: the touch may block briefly on the file system. Safe without its
            // own lease: this path only runs from a write, whose activity lease is still held —
            // no cleanup pass can overlap the walk.
            Touch(absoluteFolderPath);
        }
    }

    private void Fire(string path, Entry entry, long generation)
    {
        ITimer? fired;

        lock (_gate)
        {
            if (_disposed || entry.Generation != generation || !_entries.ContainsKey(path))
            {
                return;
            }

            fired = entry.Timer;
        }

        // The timer that fired; a request re-arming during our walk replaces entry.Timer with a
        // fresh one, and disposing the old timer twice is harmless.
        fired?.Dispose();

        _ = TouchWhenIdleAsync(path, entry, generation);
    }

    private async Task TouchWhenIdleAsync(string path, Entry entry, long generation)
    {
        try
        {
            // The walk excludes cleanup (it enters the shared activity side, cancelling a running
            // pass), so the pending entry below cannot be observed cleared mid-pass.
            using var lease = await _cleanupGate.EnterActivityAsync(CancellationToken.None).ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed || entry.Generation != generation)
                {
                    return; // superseded while waiting — the newer timer carries the touch
                }
            }

            // Outside the lock: the touch may block briefly on the file system.
            Touch(path);

            lock (_gate)
            {
                if (!_disposed && entry.Generation == generation && _entries.Remove(path))
                {
                    // Cleared only now, after the walk landed: until this point a cleanup pass
                    // still treats the folder as just-accessed and skips it. A failed walk leaves
                    // the entry pending — the folder then stays older (the safe direction) until
                    // the next successful touch or dispose.
                    entry.Timer?.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // CancellationToken.None — unreachable; keeps the timer path exception-free.
        }
        catch (Exception)
        {
            // A marker walk must never surface on the timer path; the entry stays pending and
            // the folder is simply spared by the next cleanup pass too.
        }
    }

    /// <summary>
    /// Whether a debounced touch is still pending for <paramref name="absoluteFolderPath"/>
    /// itself or anything under it — i.e. that subtree was just written to and its activity
    /// markers have not landed yet. A cleanup pass skips such a folder: deleting it would take
    /// the fresh file with it.
    /// </summary>
    public bool HasPendingWithin(string absoluteFolderPath)
    {
        ArgumentNullException.ThrowIfNull(absoluteFolderPath);

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            foreach (var candidate in _entries.Keys)
            {
                if (PathGuard.IsInside(absoluteFolderPath, candidate))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void Touch(string path)
    {
        var current = path;

        // Walk up: the written folder first, then its ancestors — the levels a nested cleanup
        // ages. The root itself never gets its marker moved (it is nobody's candidate).
        while (current is not null
               && !string.Equals(current, _rootPath, StringComparison.Ordinal)
               && PathGuard.IsInside(_rootPath, current))
        {
            try
            {
                Directory.SetLastWriteTimeUtc(current, _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // The folder vanished between the write and the debounce — keep walking: the
                // ancestors above it may well still exist and still deserve their marker.
            }

            current = Path.GetDirectoryName(current);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var entry in _entries.Values)
            {
                entry.Generation++; // invalidate callbacks already in flight
                entry.Timer?.Dispose();
            }

            _entries.Clear();
        }
    }

    /// <summary>Per-folder timer slot; <see cref="Generation"/> invalidates superseded callbacks.</summary>
    private sealed class Entry
    {
        public ITimer? Timer;
        public long Generation;
    }
}
