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
    bool IsActive { get; }

    /// <summary>
    /// 
    /// </summary>
    int ConcurrencyLimit { get; set; }

    /// <summary>
    /// Gets a change token that can be used to track changes to the active state of the scheduler.
    /// </summary>
    IChangeToken GetActiveChangeToken();

    /// <summary>
    /// Starts the job scheduler asynchronously.
    /// </summary>
    Task<bool> Start(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the job scheduler asynchronously.
    /// </summary>
    Task<bool> Restart();

    /// <summary>
    /// Stops the job scheduler asynchronously.
    /// </summary>
    Task Stop();
}
