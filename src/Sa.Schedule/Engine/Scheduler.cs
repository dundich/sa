namespace Sa.Schedule.Engine;

internal sealed class Scheduler(IScheduleSettings settings, IJobFactory factory)
    : IScheduler, IDisposable, IAsyncDisposable
{
    private volatile bool _disposed;

    public IScheduleSettings Settings => settings;

    public IReadOnlyCollection<IJobScheduler> Schedules { get; } = [.. settings
        .GetJobSettings()
        .Select(factory.CreateJobSchedule)];

    /// <summary>
    /// Start all jobs
    /// </summary>
    public async Task<int> Start(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(Schedules.Select(c => c.Start(cancellationToken)));
        return results.Count(r => r);
    }

    public async Task<int> Restart(CancellationToken cancellationToken)
    {
        var results = await Task
            .WhenAll(Schedules
                .Where(c => c.IsStarted)
                .Select(c => Task.Run(async () =>
                {
                    await c.Stop();
                    await c.Start(cancellationToken);
                    return true;
                })));

        return results.Count(r => r);
    }

    public async Task Stop()
    {
        await Task.WhenAll(Schedules.Select(c => c.Stop()));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            foreach (var job in Schedules)
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
            foreach (var job in Schedules)
            {
                await job.DisposeAsync();
            }
        }
    }
}
