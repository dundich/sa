using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Represents the settings for cleaning up old Outbox messages and parts in PostgreSQL.
/// This class contains configuration options for how and when the cleanup should occur.
/// </summary>
public sealed class PgOutboxCleanupSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the cleanup should be executed as a background job.
    /// Default is set to true, meaning the cleanup runs as a background job by default.
    /// </summary>
    public bool AsBackgroundJob { get; set; } = true;

    /// <summary>
    /// Gets or sets the duration after which old parts will be dropped.
    /// Default is set to 30 days.
    /// </summary>
    public TimeSpan DropPartsAfterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets how expired parts are retired. Default is <see cref="PartCleanupMode.Drop"/> —
    /// the historical behaviour. <see cref="PartCleanupMode.Archive"/> moves them into
    /// <see cref="ArchiveSchemaName"/> instead of dropping them.
    /// </summary>
    public PartCleanupMode CleanupMode { get; set; } = PartCleanupMode.Drop;

    /// <summary>
    /// Gets or sets the schema that archived parts are moved into when
    /// <see cref="CleanupMode"/> is <see cref="PartCleanupMode.Archive"/>. Default is <c>archive</c>.
    /// </summary>
    public string ArchiveSchemaName { get; set; } = "archive";

    /// <summary>
    /// Gets or sets the interval at which the cleanup job will be executed.
    /// Default is set to every 4 hours.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan.FromHours(4);
}
