namespace Sa.Schedule.Engine;

internal sealed class Scheduler(IScheduleSettings settings, IJobFactory factory) : IScheduler, IDisposable, IAsyncDisposable
{
    private bool _disposed;

    public IScheduleSettings Settings => settings;

    public IReadOnlyCollection<IJobScheduler> Schedules { get; } = [.. settings
        .GetJobSettings()
        .Select(factory.CreateJobSchedule)];

    /// <summary>
    /// Start all jobs
    /// </summary>
    public int Start(CancellationToken cancellationToken) => Schedules.Count(c => c.Start(cancellationToken));

    public int Restart() => Schedules.Count(c => c.Restart());

    public async Task Stop() => await Task.WhenAll(Schedules.Select(c => c.Stop()));

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = Stop();
        }
    }

    public async ValueTask DisposeAsync() => await Stop();
}
