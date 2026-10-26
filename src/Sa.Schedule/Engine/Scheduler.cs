namespace Sa.Schedule.Engine;

internal sealed class Scheduler(IScheduleSettings settings, IJobFactory factory)
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
    public async Task<int> Start(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(
            Jobs
                .Select(c => c.Start(cancellationToken)));
        return results.Count(r => r);
    }

    public async Task<int> Restart(CancellationToken cancellationToken)
    {
        // Restart only what is running (a stopped job — error-aborted, run-once
        // consumed — is not brought back), and count the jobs that actually started
        // again rather than the ones that were merely stopped.
        IJobScheduler[] jobs = [.. Jobs.Where(c => c.IsStarted)];

        int[] results = await Task.WhenAll(jobs.Select(c => RestartJob(c, cancellationToken)));

        return results.Count(r => r == 1);
    }

    private static async Task<int> RestartJob(IJobScheduler job, CancellationToken cancellationToken)
    {
        await job.Stop();
        return await job.Start(cancellationToken) ? 1 : 0;
    }

    public async Task Stop()
    {
        await Task.WhenAll(Jobs.Select(c => c.Stop()));
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
            await Stop();
            foreach (var job in Jobs)
            {
                await job.DisposeAsync();
            }
        }
    }
}
