using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal sealed class Scheduler(
    IScheduleSettings settings,
    IJobFactory factory,
    ILogger<Scheduler>? logger = null)
    : IScheduler, IDisposable, IAsyncDisposable
{
    private volatile bool _disposed;

    public IScheduleSettings Settings => settings;

    public IReadOnlyCollection<IJobScheduler> Jobs { get; } = [.. settings
        .GetJobSettings()
        .Select(factory.CreateJobSchedule)];

    /// <summary>
    /// Start all jobs
    /// </summary>
    public async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            Jobs
                .Select(c => c.StartAsync(cancellationToken)));
        return results.Count(r => r);
    }

    public async Task<int> RestartAsync(CancellationToken cancellationToken)
    {
        // Restart only what is running (a stopped job — error-aborted, run-once
        // consumed — is not brought back), and count the jobs that actually started
        // again rather than the ones that were merely stopped.
        IJobScheduler[] jobs = [.. Jobs.Where(c => c.IsStarted)];

        int[] results = await Task.WhenAll(jobs.Select(c => RestartJobAsync(c, cancellationToken)));

        return results.Count(r => r == 1);
    }

    private async Task<int> RestartJobAsync(IJobScheduler job, CancellationToken cancellationToken)
    {
        try
        {
            await job.StopAsync();
            return await job.StartAsync(cancellationToken) ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled the restart — do not disguise it as a broken job.
            throw;
        }
        catch (Exception ex)
        {
            // One job's teardown must not prevent the others from being restarted.
            if (logger is not null)
            {
                SchedulerLog.LogJobRestartFailed(logger, job.JobId, ex);
            }

            return 0;
        }
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(Jobs.Select(c => c.StopAsync()));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            foreach (var job in Jobs)
            {
                job.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await StopAsync();
            foreach (var job in Jobs)
            {
                await job.DisposeAsync();
            }
        }
    }
}
