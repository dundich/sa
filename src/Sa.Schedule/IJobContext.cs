using Microsoft.Extensions.Logging;

namespace Sa.Schedule;

/// <summary>
/// Provides information about the job context.
/// </summary>
/// <remarks>
/// This interface defines the properties and methods that are available for a job context.
/// </remarks>
/// <seealso cref="IJobSettings"/>
public interface IJobContext
{
    string JobName { get; }

    IJobSettings Settings { get; }
    ulong NumIterations { get; }
    ulong FailedIterations { get; }
    ulong CompletedIterations { get; }
    int FailedRetries { get; }
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset? ExecuteAt { get; }
    JobException? LastError { get; }
    IServiceProvider ServiceProvider { get; }

    /// <summary>
    /// The previous context snapshots (newest first), up to the configured
    /// <see cref="IJobProperties.ContextStackSize"/>.
    /// </summary>
    IEnumerable<IJobSnapshot> Stack { get; }

    ILogger Logger { get; }
}
