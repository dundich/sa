using Microsoft.Extensions.Logging;

namespace Sa.Schedule;

/// <summary>
/// Provides information about the job context.
/// </summary>
/// <remarks>
/// This interface defines the properties and methods that are available for a job context.
/// </remarks>
/// <seealso cref="IJobSettings"/>
/// <seealso cref="JobStatus"/>
public interface IJobContext
{

    Guid JobId { get; }
    string JobName { get; }
    JobStatus Status { get; }
    IJobSettings Settings { get; }
    ulong NumIterations { get; }
    ulong FailedIterations { get; }
    ulong CompetedIterations { get; }
    int FailedRetries { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? ExecuteAt { get; }
    JobException? LastError { get; }
    IServiceProvider JobServices { get; }
    IEnumerable<IJobContext> Stack { get; }
    ILogger Logger { get; }

    IJobContext Clone();
    bool Active => Status == JobStatus.Running || Status == JobStatus.WaitingToRun;
}