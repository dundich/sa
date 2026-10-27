namespace Sa.Outbox.PostgreSql.Configuration;

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

    /// <summary>
    /// Rejects table and schema names that cannot be spliced into the generated DDL safely, and
    /// consumer group ids whose partition names could never fit PostgreSQL's 63-byte limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Column definitions are <b>developer-provided trusted input</b> here: the <c>Fields</c> tables
    /// hand out raw <c>name TYPE …</c> fragments (they even carry <c>--</c> comments) and the builder
    /// pastes them into a comma-separated column list, so they cannot be pattern-checked — a
    /// definition legitimately contains semicolons and quotes. The table and schema names are the
    /// identifiers this library owns, and they are checked here.
    /// </para>
    /// <para>
    /// Called once, when the settings are materialised from the container (see
    /// <c>PgOutboxConfiguration.RegisterOutboxSettings</c>), i.e. after every
    /// <c>WithOutboxSettings</c> action has run and before any SQL template is built.
    /// </para>
    /// </remarks>
    /// <param name="consumerGroupIds">The registered consumer groups, when the delivery side is in use.</param>
    /// <exception cref="ArgumentException">A table, schema or consumer group name is unusable.</exception>
    internal void Validate(IReadOnlyCollection<string>? consumerGroupIds = null)
        => TableSettings.Validate(consumerGroupIds);
}
