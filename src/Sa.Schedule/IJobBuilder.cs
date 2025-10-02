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
    IJobBuilder EverySeconds(int seconds = 1) => EveryTime(TimeSpan.FromSeconds(seconds), $"every {seconds} seconds");

    /// <summary>
    /// Configures the job to run every specified number of minutes.
    /// </summary>
    /// <param name="minutes">The number of minutes (default is 1).</param>
    /// <returns>The current builder instance.</returns>
    IJobBuilder EveryMinutes(int minutes = 1) => EveryTime(TimeSpan.FromMinutes(minutes), $"every {minutes} minutes");

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