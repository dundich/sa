namespace Sa.Outbox.Delivery;

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
