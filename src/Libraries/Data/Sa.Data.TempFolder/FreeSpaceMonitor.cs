using Microsoft.Extensions.Logging;

namespace Sa.Data.TempFolder;

/// <summary>
/// One free-space check for a temp-folder instance: reads how many bytes are left on the volume
/// holding the instance root and raises the
/// <see cref="TempFolderOptions.OnFreeSpaceReached"/> event edge-triggered when the reading
/// reaches <see cref="TempFolderOptions.MinFreeSpace"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> Nothing is ever probed while <see cref="TempFolderOptions.MinFreeSpace"/>
/// is <c>0</c>; the background service only schedules the loop when the limit is positive.
/// </para>
/// <para>
/// <b>Edge-triggered event.</b> The delegate fires once when the available space drops to or below
/// the limit and once when it climbs back above — never repeatedly while the state holds, so a
/// 24-hour poll cannot spam the subscriber. Handler exceptions are caught and logged; they never
/// break the check. A probe failure (volume gone, permission denied) is propagated to the caller —
/// the background loop logs it and simply retries at the next interval.
/// </para>
/// <para>
/// The measurement itself is a single volume stat (no tree walk, no thread), so it runs inline on
/// the loop. The free-space reader is injectable for tests; production uses
/// <see cref="DriveInfo"/> against the root's volume.
/// </para>
/// </remarks>
internal sealed class FreeSpaceMonitor
{
    private readonly string _name;
    private readonly string _rootPath;
    private readonly ILogger _logger;
    private readonly Func<string, long> _readAvailableFreeSpace;

    private readonly Lock _gate = new();
    private bool? _reached;

    public FreeSpaceMonitor(
        string name,
        string rootPath,
        ILogger logger,
        Func<string, long>? readAvailableFreeSpace = null)
    {
        _name = name;
        _rootPath = rootPath;
        _logger = logger;
        _readAvailableFreeSpace = readAvailableFreeSpace ?? ReadAvailableFreeSpace;
    }

    /// <summary>
    /// Runs one check: probes the free space and evaluates the edge-triggered event.
    /// Returns the measured free space in bytes.
    /// </summary>
    /// <exception cref="IOException">The volume could not be probed — the next interval retries.</exception>
    public long Check(TempFolderOptions options)
    {
        var available = _readAvailableFreeSpace(_rootPath);
        EvaluateTransition(options, available);
        return available;
    }

    /// <summary>Fires <see cref="TempFolderOptions.OnFreeSpaceReached"/> on state transitions only.</summary>
    private void EvaluateTransition(TempFolderOptions options, long available)
    {
        bool transitionedTo;
        lock (_gate)
        {
            if (options.MinFreeSpace <= 0)
            {
                _reached = null;
                return;
            }

            var reached = available <= options.MinFreeSpace;

            if (_reached is null)
            {
                // First reading of this (re-enabled) monitor: record the state. Already at the
                // limit from the start — that is worth reporting; above it — nothing has been
                // "reached" yet, so stay silent instead of announcing a recovery that never was.
                _reached = reached;
                if (!reached)
                {
                    return;
                }

                transitionedTo = true;
            }
            else
            {
                if (_reached == reached)
                {
                    return; // state holds — edge-triggered, no repeat
                }

                _reached = reached;
                transitionedTo = reached;
            }
        }

        var args = new TempFolderFreeSpaceArgs(_name, _rootPath, available, options.MinFreeSpace, transitionedTo);

        if (transitionedTo)
        {
            _logger.LogWarning(
                "Temp folder '{Name}' free space {Available} bytes reached the limit {Min} bytes (root {Root}).",
                _name, available, options.MinFreeSpace, _rootPath);
        }
        else
        {
            _logger.LogInformation(
                "Temp folder '{Name}' free space {Available} bytes is back above the limit {Min} bytes (root {Root}).",
                _name, available, options.MinFreeSpace, _rootPath);
        }

        var handler = options.OnFreeSpaceReached;
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
            _logger.LogWarning(ex, "Temp folder '{Name}' OnFreeSpaceReached handler threw.", _name);
        }
    }

    /// <summary>
    /// Free space of the volume the root lives on. On Windows <see cref="DriveInfo"/> wants a
    /// drive root (<c>C:\</c>); on Unix it stats the path itself, which keeps the reading on the
    /// actual mount rather than on <c>/</c>.
    /// </summary>
    private static long ReadAvailableFreeSpace(string rootPath)
    {
        var location = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(rootPath) ?? rootPath
            : rootPath;

        return new DriveInfo(location).AvailableFreeSpace;
    }
}
