namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Settings for the automated background job that pre-creates future partitions.
/// </summary>
public sealed class MigrationScheduleSettings
{
    /// <summary>
    /// Gets or sets how many days into the future to pre-create partitions on each run.
    /// Default is 2 days.
    /// </summary>
    public int ForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets whether the migration should run as a hosted background service (<c>true</c>)
    /// or only be triggered manually via <see cref="IMigrationService"/>.
    /// Default is <c>false</c>.
    /// </summary>
    public bool AsBackgroundJob { get; set; } = false;

    /// <summary>
    /// Gets or sets the optional name of the hosted background service job.
    /// When <c>null</c>, a default name is used.
    /// </summary>
    public string? MigrationJobName { get; set; }

    /// <summary>
    /// Gets or sets the interval between consecutive migration runs.
    /// Default is 4 hours plus a random jitter of up to 59 minutes to avoid thundering-herd effects.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

    /// <summary>
    /// Gets or sets the maximum time to wait for an in-flight migration to complete before considering it timed out.
    /// Default is 3 seconds.
    /// </summary>
    public TimeSpan WaitMigrationTimeout { get; set; } = TimeSpan.FromSeconds(3);
}
