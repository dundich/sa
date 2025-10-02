namespace Sa.Schedule;

/// <summary>
/// Represents a job timing interface.
/// </summary>
public interface IJobTiming
{
    /// <summary>
    /// Gets the name of the timing.
    /// </summary>
    string TimingName { get; }

    /// <summary>
    /// Gets the next occurrence of the job timing.
    /// </summary>
    /// <param name="dateTime">The current date and time.</param>
    /// <param name="context">The job context.</param>
    /// <returns>The next occurrence of the job timing, or null if no next occurrence is found.</returns>
    DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime, IJobContext context);
}