namespace Sa.Schedule;

/// <summary>
/// Defines a builder for creating and configuring jobs.
/// </summary>
public interface IJobBuilder
{
    /// <summary>
    /// Sets the name of the job.
    /// </summary>
    /// <param name="name">The name of the job.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithName(string name);

    /// <summary>
    /// Starts the job immediately.
    /// </summary>
    /// <returns>The current builder instance.</returns>
    IJobBuilder StartImmediate();

    /// <summary>
    /// Configures the job to run once.
    /// </summary>
    /// <returns>The current builder instance.</returns>
    IJobBuilder RunOnce();

    /// <summary>
    /// Sets the context stack size for the job.
    /// </summary>
    /// <param name="size">The context stack size.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithContextStackSize(int size);

    /// <summary>
    /// job schedule delay before start
    /// </summary>
    /// <param name="delay">The delay time span.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithInitialDelay(TimeSpan delay);

    /// <summary>
    /// Sets the timing for the job.
    /// </summary>
    /// <param name="timing">The job timing.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithTiming(IJobTiming timing);

    /// <summary>
    /// Sets a tag for the job.
    /// </summary>
    /// <param name="tag">The job tag.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithTag(object tag);

    /// <summary>
    /// Configures the job to run at the specified time interval.
    /// </summary>
    /// <param name="timeSpan">The time interval.</param>
    /// <param name="name">The name of the interval (optional).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EveryTime(TimeSpan timeSpan, string? name = null);

    /// <summary>
    /// Configures the job to run every specified number of seconds.
    /// </summary>
    /// <param name="seconds">The number of seconds (default is 1).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EverySeconds(int seconds = 1)
        => EveryTime(TimeSpan.FromSeconds(seconds), $"every {seconds} seconds");

    /// <summary>
    /// Configures the job to run every specified number of minutes.
    /// </summary>
    /// <param name="minutes">The number of minutes (default is 1).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EveryMinutes(int minutes = 1)
        => EveryTime(TimeSpan.FromMinutes(minutes), $"every {minutes} minutes");

    /// <summary>
    /// Configures the job to run every specified number of hours.
    /// </summary>
    /// <param name="hours">The number of hours (default is 1).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EveryHours(int hours = 1)
        => EveryTime(TimeSpan.FromHours(hours), $"every {hours} hours");

    /// <summary>
    /// Configures the job to run every specified number of days.
    /// </summary>
    /// <param name="days">The number of days (default is 1).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EveryDays(int days = 1)
        => EveryTime(TimeSpan.FromDays(days), $"every {days} days");

    /// <summary>
    /// Configures the job to run once after the specified delay.
    /// </summary>
    /// <param name="delay">The delay before the single execution.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder OnceIn(TimeSpan delay)
        => WithInitialDelay(delay).RunOnce();

    /// <summary>
    /// Configures the job using a cron expression for scheduling.
    /// Format: "minute hour day-of-month month day-of-week"
    /// 
    /// Examples:
    ///   "0 9 * * *"       — Every day at 9:00 AM
    ///   "0 */2 * * *"    — Every 2 hours at minute 0
    ///   "30 14 * * 1-5"  — Weekdays (Mon-Fri) at 2:30 PM
    ///   "0 0 1 * *"      — First day of every month at midnight
    /// </summary>
    /// <param name="cronExpression">The cron expression.</param>
    /// <param name="name">Optional display name for the timing.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithCron(string cronExpression, string? name = null);


    IJobBuilder WithConcurrencyLimit(int limit);

    /// <summary>
    /// Sets the maximum concurrency limit for the job.
    /// </summary>
    /// <param name="limit">The maximum concurrency limit.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithMaxConcurrency(int limit);

    /// <summary>
    /// Sets the maximum concurrency limit for the job (alias for <see cref="WithMaxConcurrency"/>).
    /// </summary>
    /// <param name="limit">The maximum concurrency limit.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder WithMaxConcurrencyLimit(int limit) => WithMaxConcurrency(limit);


    /// <summary>
    /// Merges the specified job properties into the current job configuration.
    /// </summary>
    /// <param name="props">The job properties to merge.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder Merge(IJobProperties props);

    /// <summary>
    /// Configures error handling for the job.
    /// </summary>
    /// <param name="configure">The error handling configuration action.</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder ConfigureErrorHandling(Action<IJobErrorHandlingBuilder> configure);

    /// <summary>
    /// Disables the job.
    /// </summary>
    /// <returns>The current builder instance.</returns>
    IJobBuilder Disabled();
}
