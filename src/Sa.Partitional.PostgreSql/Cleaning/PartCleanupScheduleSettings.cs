namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents the settings for scheduling the cleanup of Outbox message parts.
/// This class contains configuration options related to how and when parts should be cleaned up.
/// </summary>
public class PartCleanupScheduleSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the cleanup should be executed as a background job.
    /// Default is set to false, meaning the cleanup will not run as a job.
    /// </summary>
    public bool AsJob { get; set; } = false;

    /// <summary>
    /// Gets or sets the duration after which old parts will be dropped.
    /// Default is set to 30 days.
    /// </summary>
    public TimeSpan DropPartsAfterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the interval at which the cleanup job will be executed.
    /// Default is set to every 4 hours, with a random additional delay of up to 59 minutes.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

    /// <summary>
    /// Gets or sets the initial delay before the cleanup job starts executing.
    /// Default is set to 1 minute.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);
}
