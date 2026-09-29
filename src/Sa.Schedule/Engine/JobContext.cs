using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;

internal sealed class JobContext(IJobSettings settings) : IJobContext
{
    public string JobName => settings.Properties.JobName ?? $"{settings.JobId}";

    public IJobSettings Settings => settings;

    public ulong NumIterations { get; set; }

    public ulong FailedIterations { get; set; }

    public ulong CompletedIterations { get; set; }

    public int FailedRetries { get; set; }

    public JobException? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExecuteAt { get; set; }

    public ulong NumRuns { get; set; }

    // Previous context snapshots, oldest first. The queue is capped by the
    // consumer (CanExecute) to the configured ContextStackSize.
    public Queue<IJobSnapshot> Stack { get; private set; } = [];

    IEnumerable<IJobSnapshot> IJobContext.Stack => Stack.Reverse();

    public IServiceProvider ServiceProvider { get; set; } = NullJobServices.Instance;

    public ILogger Logger => ServiceProvider.GetService<ILogger<JobContext>>()
        ?? NullLogger<JobContext>.Instance;

    internal IJobSnapshot ToSnapshot() => new JobSnapshot(this);
}
