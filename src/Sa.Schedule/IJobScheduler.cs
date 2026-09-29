using Microsoft.Extensions.Primitives;

namespace Sa.Schedule;

/// <summary>
/// This individual task scheduler is responsible for managing specific tasks.
/// </summary>
public interface IJobScheduler: IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of the job.
    /// </summary>
    Guid JobId { get; }

    /// <summary>
    /// Gets a value indicating whether the job scheduler is currently active.
    /// </summary>
    bool IsStarted { get; }

    /// <summary>
    /// Gets the number of tasks currently in the queue buffer.
    /// While the job is running this equals the number of pre-allocated slots
    /// (the job's <c>MaxConcurrency</c>); it is <c>0</c> when the job is
    /// stopped, disabled, or has not been started yet.
    /// </summary>
    int QueueTasks { get; }

    /// <summary>
    /// Consume instance count
    /// </summary>
    int ConcurrencyLimit { get; set; }

    /// <summary>
    /// Gets a change token that can be used to track changes to the active state of the scheduler.
    /// </summary>
    IChangeToken StartChangeToken();

    /// <summary>
    /// Starts the job scheduler asynchronously.
    /// </summary>
    Task<bool> Start(CancellationToken cancellationToken);

    /// <summary>
    /// Stops the job scheduler asynchronously.
    /// </summary>
    Task Stop();
}
