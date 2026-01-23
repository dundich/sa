namespace Sa.Schedule;

/// <summary>
/// This scheduler that manages multiple job schedulers.
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Gets the schedule settings.
    /// </summary>
    IScheduleSettings Settings { get; }

    /// <summary>
    /// Gets the collection of job schedulers.
    /// </summary>
    IReadOnlyCollection<IJobScheduler> Schedules { get; }

    /// <summary>
    /// Starts the scheduler.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of jobs started.</returns>
    int Start(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the scheduler.
    /// </summary>
    /// <returns>The number of jobs restarted.</returns>
    int Restart();

    /// <summary>
    /// Stops the scheduler.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Stop();
}
