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

    public ulong CompetedIterations { get; set; }

    public int FailedRetries { get; set; }

    public JobException? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExecuteAt { get; set; }

    public ulong NumRuns { get; set; }

    public Queue<IJobContext> Stack { get; private set; } = [];

    IEnumerable<IJobContext> IJobContext.Stack => Stack.Reverse();

    public IServiceProvider ServiceProvider { get; set; } = NullJobServices.Instance;

    public ILogger Logger => ServiceProvider.GetService<ILogger<JobContext>>()
        ?? NullLogger<JobContext>.Instance;

    public IJobContext Clone()
    {
        JobContext clone = new(Settings)
        {
            NumIterations = NumIterations,
            FailedRetries = FailedRetries,
            FailedIterations = FailedIterations,
            LastError = LastError,
            CreatedAt = CreatedAt,
            ExecuteAt = ExecuteAt,
            NumRuns = NumRuns,
            Stack = new Queue<IJobContext>(Stack.Select(x => x.Clone())),
        };

        return clone;
    }
}
