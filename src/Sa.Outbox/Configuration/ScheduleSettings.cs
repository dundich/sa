namespace Sa.Outbox;

/// <summary>
/// Represents the scheduling settings for the delivery job.
/// </summary>
public sealed class ScheduleSettings
{
    /// <summary>
    /// Gets the unique identifier for the delivery job
    /// </summary>
    public Guid JobId { get; } = Guid.NewGuid();

    public string? Name { get; internal set; }

    public TimeSpan Interval { get; internal set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Job schedule delay before start
    /// </summary>
    public TimeSpan InitialDelay { get; internal set; } = TimeSpan.FromSeconds(10);

    public int RetryCountOnError { get; internal set; } = 2;
}


public static class ScheduleSettingsExtensions
{
    /// <summary>
    /// Sets the job name.
    /// </summary>
    public static ScheduleSettings WithName(this ScheduleSettings settings, string name)
    {
        settings.Name = name;
        return settings;
    }

    /// <summary>
    /// Sets the interval between job executions.
    /// </summary>
    /// <param name="interval">The execution interval.</param>
    /// <returns>This instance for chaining.</returns>
    public static ScheduleSettings WithInterval(this ScheduleSettings settings, TimeSpan interval)
    {
        settings.Interval = interval;
        return settings;
    }

    /// <summary>
    /// Sets the initial delay before the first job execution.
    /// </summary>
    /// <param name="delay">The initial delay.</param>
    /// <returns>This instance for chaining.</returns>
    public static ScheduleSettings WithInitialDelay(this ScheduleSettings settings, TimeSpan delay)
    {
        settings.InitialDelay = delay;
        return settings;
    }

    public static ScheduleSettings WithImmediate(this ScheduleSettings settings)
    {
        settings.InitialDelay = TimeSpan.Zero;
        return settings;
    }

    /// <summary>
    /// Sets the number of retry attempts on error.
    /// </summary>
    /// <param name="retryCount">Number of retries.</param>
    /// <returns>This instance for chaining.</returns>
    public static ScheduleSettings WithRetryCountOnError(this ScheduleSettings settings, int retryCount)
    {
        settings.RetryCountOnError = retryCount;
        return settings;
    }

    /// <summary>
    /// Configures the job with no retries on error.
    /// </summary>
    public static ScheduleSettings WithNoRetries(this ScheduleSettings settings)
    {
        return settings.WithRetryCountOnError(0);
    }

    /// <summary>
    /// Configures the job with infinite retries on error.
    /// </summary>
    public static ScheduleSettings WithInfiniteRetries(this ScheduleSettings settings)
    {
        return settings.WithRetryCountOnError(int.MaxValue);
    }

    /// <summary>
    /// Configures test settings.
    /// </summary>
    public static ScheduleSettings UseTestSettings(
        this ScheduleSettings settings)
    {
        return settings
            .WithName($"Test-DeliveryJob-{settings.JobId}")
            .WithInterval(TimeSpan.FromMilliseconds(300))
            .WithImmediate();
    }

    public static ScheduleSettings WithIntervalSeconds(this ScheduleSettings settings, int seconds)
        => settings.WithInterval(TimeSpan.FromSeconds(seconds));

    public static ScheduleSettings WithIntervalMilliseconds(this ScheduleSettings settings, int milliseconds)
        => settings.WithInterval(TimeSpan.FromMilliseconds(milliseconds));

    public static ScheduleSettings WithIntervalMinutes(this ScheduleSettings settings, int minutes)
        => settings.WithInterval(TimeSpan.FromMinutes(minutes));

    public static ScheduleSettings WithInitialDelaySeconds(this ScheduleSettings settings, int seconds)
        => settings.WithInitialDelay(TimeSpan.FromSeconds(seconds));

    public static ScheduleSettings WithInitialDelayMinutes(this ScheduleSettings settings, int minutes)
        => settings.WithInitialDelay(TimeSpan.FromMinutes(minutes));
}