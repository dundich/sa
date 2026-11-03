using Microsoft.Extensions.Logging;

namespace Sa.Schedule;

/// <summary>
/// Provides information about the job context.
/// </summary>
/// <remarks>
/// This interface defines the properties and methods that are available for a job context.
/// <para>
/// Counters (<see cref="NumIterations"/>, <see cref="FailedIterations"/>,
/// <see cref="CompletedIterations"/>, <see cref="NumRuns"/>, <see cref="CreatedAt"/>) are
/// <b>per slot</b>: when the job runs with <c>MaxConcurrency</c> &gt; 1 every concurrent
/// slot has its own context, so the numbers describe one slot's work, not the job's as a
/// whole, and they are not aggregated across slots.
/// </para>
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
