using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sa.Data.TempFolder;

/// <summary>
/// Background service behind <see cref="Setup.AddSaTempFolder"/>: verifies every registered
/// instance's root at host start (fail fast), then runs each instance's periodic cleanup pass and
/// — when configured — its independent low-priority volume scan and free-space check.
/// </summary>
/// <remarks>
/// One host per service collection walks <b>all</b> registrations. Read-only instances are
/// skipped entirely: no cleanup, no scan, no volume event, no free-space check. Loop failures are
/// logged and never bring the host down — the next interval simply runs the pass again. All delays
/// go through the container's <see cref="TimeProvider"/>, so tests can virtualise the schedule.
/// </remarks>
internal sealed class TempFolderCleanerHost : BackgroundService
{
    private readonly IEnumerable<TempFolderRegistration> _registrations;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public TempFolderCleanerHost(IEnumerable<TempFolderRegistration> registrations, IServiceProvider services)
    {
        _registrations = registrations;
        _services = services;
        _timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<TempFolderCleanerHost>();
    }

    /// <summary>
    /// Access-checks every registered root <b>before</b> the loops start: a root that cannot be
    /// listed (or written, for read-write instances) fails host startup with a clear message
    /// instead of surfacing at first use.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _registrations)
        {
            var folder = _services.GetRequiredKeyedService<ITempFolder>(registration.Name);
            await folder.CheckAccessAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts one cleanup loop per instance and, when configured, its scan / free-space loops.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>();

        foreach (var registration in _registrations)
        {
            if (_services.GetRequiredKeyedService<ITempFolder>(registration.Name) is not TempFolder folder)
            {
                continue;
            }

            var options = folder.OptionsSnapshot;

            if (options.ReadOnly)
            {
                _logger.LogDebug(
                    "Temp folder '{Name}' is read-only; the background service ignores it.", registration.Name);
                continue;
            }

            loops.Add(RunLoopAsync(
                $"{registration.Name}:cleanup",
                options.CleanupInterval,
                runFirstImmediately: false,
                () => folder.CleanupAsync(stoppingToken).AsTask(),
                stoppingToken));

            if (folder.HasPeriodicVolumeScan())
            {
                loops.Add(RunLoopAsync(
                    $"{registration.Name}:volume-scan",
                    options.VolumeScanInterval,
                    runFirstImmediately: true,
                    () => folder.ScanVolumeAsync(stoppingToken),
                    stoppingToken));
            }

            if (folder.HasFreeSpaceCheck())
            {
                // Opt-in (MinFreeSpace > 0): probed immediately at start — an already-tight volume
                // must surface at once, not after the first interval — then once per interval.
                loops.Add(RunLoopAsync(
                    $"{registration.Name}:free-space",
                    options.FreeSpaceCheckInterval,
                    runFirstImmediately: true,
                    () =>
                    {
                        folder.CheckFreeSpace();
                        return Task.CompletedTask;
                    },
                    stoppingToken));
            }
        }

        if (loops.Count == 0)
        {
            return; // nothing to do (e.g. every instance is read-only)
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>
    /// One loop: optionally an immediate first pass, then <paramref name="interval"/> between
    /// passes. Failures inside a pass are logged; the loop keeps its schedule.
    /// </summary>
    private async Task RunLoopAsync(
        string loopName, TimeSpan interval, bool runFirstImmediately, Func<Task> pass, CancellationToken stoppingToken)
    {
        if (runFirstImmediately)
        {
            await RunPassAsync(loopName, pass, stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RunPassAsync(loopName, pass, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunPassAsync(string loopName, Func<Task> pass, CancellationToken stoppingToken)
    {
        try
        {
            await pass().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp folder background loop '{Loop}' failed; it will retry next interval.", loopName);
        }
    }
}
