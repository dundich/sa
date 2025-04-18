namespace Sa.Schedule;

/// <summary>
/// Defines the settings for a schedule.
/// </summary>
public interface IScheduleSettings
{
    /// <summary>
    /// Gets a value indicating whether the schedule is hosted as a service.
    /// </summary>
    bool IsHostedService { get; }

    /// <summary>
    /// Gets a function that handles errors that occur during job execution.
    /// </summary>
    Func<IJobContext, Exception, bool>? HandleError { get; }

    /// <summary>
    /// Gets a collection of job settings.
    /// </summary>
    IEnumerable<IJobSettings> GetJobSettings();
}
