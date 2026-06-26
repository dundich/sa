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

    /// <summary>
    /// Creates a cron-based timing from a standard 5-field cron expression.
    /// Format: "minute hour day-of-month month day-of-week"
    /// 
    /// Examples:
    ///   "0 9 * * *"       — Every day at 9:00 AM
    ///   "0 */2 * * *"    — Every 2 hours at minute 0
    ///   "30 14 * * 1-5"  — Weekdays (Mon-Fri) at 2:30 PM
    ///   "0 0 1 * *"      — First day of every month at midnight
    /// </summary>
    /// <param name="expression">The cron expression.</param>
    /// <param name="name">Optional display name.</param>
    /// <returns>An IJobTiming configured with cron scheduling.</returns>
    static IJobTiming FromCron(string expression, string? name = null)
        => new Engine.CronTiming(expression, name);
}
