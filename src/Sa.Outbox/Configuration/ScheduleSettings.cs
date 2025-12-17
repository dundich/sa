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

    public string? Name { get; private set; }

    public TimeSpan Interval { get; private set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Job schedule delay before start
    /// </summary>
    public TimeSpan InitialDelay { get; private set; } = TimeSpan.FromSeconds(10);

    public int RetryCountOnError { get; private set; } = 2;

    // Fluent методы

    /// <summary>
    /// Sets the name of the job.
    /// </summary>
    /// <param name="name">The job name.</param>
    /// <returns>This instance for chaining.</returns>
    public ScheduleSettings WithName(string? name)
    {
        Name = name;
        return this;
    }

    /// <summary>
    /// Sets the interval between job executions.
    /// </summary>
    /// <param name="interval">The execution interval.</param>
    /// <returns>This instance for chaining.</returns>
    public ScheduleSettings WithInterval(TimeSpan interval)
    {
        Interval = interval;
        return this;
    }

    /// <summary>
    /// Sets the initial delay before the first job execution.
    /// </summary>
    /// <param name="delay">The initial delay.</param>
    /// <returns>This instance for chaining.</returns>
    public ScheduleSettings WithInitialDelay(TimeSpan delay)
    {
        InitialDelay = delay;
        return this;
    }

    public ScheduleSettings WithImmediate()
    {
        InitialDelay = TimeSpan.Zero;
        return this;
    }

    /// <summary>
    /// Sets the number of retry attempts on error.
    /// </summary>
    /// <param name="retryCount">Number of retries.</param>
    /// <returns>This instance for chaining.</returns>
    public ScheduleSettings WithRetryCountOnError(int retryCount)
    {
        RetryCountOnError = retryCount;
        return this;
    }
}