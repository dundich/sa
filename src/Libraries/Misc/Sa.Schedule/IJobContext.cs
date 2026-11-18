using Microsoft.Extensions.Logging;

namespace Sa.Schedule;

/// <summary>
/// Provides information about the job context.
/// </summary>
/// <remarks>
/// This interface defines the properties and methods that are available for a job context.
/// <para>
/// Counters (<see cref="NumIterations"/>, <see cref="FailedIterations"/>,
/// <see cref="CompletedIterations"/>, <c>NumRuns</c>, <see cref="CreatedAt"/>) are
/// <b>per slot</b>: when the job runs with <c>MaxConcurrency</c> &gt; 1 every concurrent
/// slot has its own context, so the numbers describe one slot's work, not the job's as a
/// whole, and they are not aggregated across slots.
/// </para>
/// </remarks>
/// <seealso cref="IJobSettings"/>
public interface IJobContext
{
    /// <summary>
    /// The name of the job.
    /// </summary>
    string JobName { get; }

    /// <summary>
    /// The effective settings of the job.
    /// </summary>
    IJobSettings Settings { get; }

    /// <summary>
    /// The number of iterations started on this slot.
    /// </summary>
    ulong NumIterations { get; }

    /// <summary>
    /// The number of failed iterations on this slot.
    /// </summary>
    ulong FailedIterations { get; }

    /// <summary>
    /// The number of successfully completed iterations on this slot.
    /// </summary>
    ulong CompletedIterations { get; }

    /// <summary>
    /// The number of failed retries on this slot.
    /// </summary>
    int FailedRetries { get; }

    /// <summary>
    /// When this context was created.
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// When the next iteration is scheduled to run, if known.
    /// </summary>
    DateTimeOffset? ExecuteAt { get; }

    /// <summary>
    /// The error raised by the previous failure, if any.
    /// </summary>
    JobException? LastError { get; }

    /// <summary>
    /// The service provider of the job's scope.
    /// </summary>
    IServiceProvider ServiceProvider { get; }

    /// <summary>
    /// The previous context snapshots (newest first), up to the configured
    /// <see cref="IJobProperties.ContextStackSize"/>.
    /// </summary>
    IEnumerable<IJobSnapshot> Stack { get; }

    /// <summary>
    /// The logger of the job's scope.
    /// </summary>
    ILogger Logger { get; }
}
