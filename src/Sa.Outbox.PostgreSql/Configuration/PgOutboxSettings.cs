namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Represents the settings for the PostgreSQL Outbox configuration.
/// This class contains various settings related to table configuration, serialization, caching, migration, and cleanup.
/// </summary>
public sealed class PgOutboxSettings
{
    /// <summary>
    /// Gets the settings related to the Outbox table configuration.
    /// </summary>
    public PgOutboxTableSettings TableSettings { get; } = new();

    /// <summary>
    /// Gets the settings related to migration of the Outbox schema.
    /// </summary>
    public PgOutboxMigrationSettings MigrationSettings { get; } = new();

    /// <summary>
    /// Gets the settings related to cleanup of old Outbox messages and parts.
    /// </summary>
    public PgOutboxCleanupSettings CleanupSettings { get; } = new();

    /// <summary>
    ///  Represents the consumption settings for retrieving & processing messages from the Outbox
    /// </summary>
    public PgOutboxConsumeSettings ConsumeSettings { get; } = new();
}
