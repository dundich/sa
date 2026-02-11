namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Represents the settings for migrating the Outbox schema in PostgreSQL.
/// </summary>
public sealed class PgOutboxMigrationSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the migration should be executed as a background job.
    /// Default is set to true, meaning the migration will run as a job.
    /// </summary>
    public bool AsBackgroundJob { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of days to move forward during the migration process.
    /// Default is set to 2 days.
    /// </summary>
    public int ForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets the interval at which the migration job will be executed.
    /// Default is set to every 6 hours, with a random additional delay of up to 59 minutes.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(6)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));
}
