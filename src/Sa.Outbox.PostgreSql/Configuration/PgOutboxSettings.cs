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
    /// Gets the settings related to caching of message types.
    /// </summary>
    public PgOutboxCacheSettings CacheSettings { get; } = new();

    /// <summary>
    /// Gets the settings related to migration of the Outbox schema.
    /// </summary>
    public PgOutboxMigrationSettings MigrationSettings { get; } = new();

    /// <summary>
    /// Gets the settings related to cleanup of old Outbox messages and parts.
    /// </summary>
    public PgOutboxCleanupSettings CleanupSettings { get; } = new();
}


/// <summary>
/// Represents the settings for configuring the Outbox tables in PostgreSQL.
/// </summary>
public sealed class PgOutboxTableSettings
{
    /// <summary>
    /// Gets or sets the name of the database schema.
    /// Default is set to "public".
    /// </summary>
    public string DatabaseSchemaName { get; set; } = "public";

    /// <summary>
    /// Gets or sets the name of the Outbox Messages table.
    /// Default is set to "outbox__$msg".
    /// </summary>
    public string DatabaseMsgTableName { get; set; } = "outbox__msg$";

    /// <summary>
    /// Gets or sets the name of the delivery table.
    /// Default is set to "outbox__$delivery".
    /// </summary>
    public string DatabaseDeliveryTableName { get; set; } = "outbox__delivery$";

    /// <summary>
    /// Gets or sets the name of the type table.
    /// Default is set to "outbox__$type".
    /// </summary>
    public string DatabaseTypeTableName { get; set; } = "outbox__type$";

    /// <summary>
    /// Gets or sets the offset for receiving group messages.
    /// Default is set to "outbox__offset$".
    /// </summary>
    public string DatabaseOffsetTableName { get; set; } = "outbox__offset$";

    /// <summary>
    /// Default is set to "outbox__task$".
    /// </summary>
    public string DatabaseTaskTableName { get; set; } = "outbox__task$";

    /// <summary>
    /// Gets or sets the name of the error table.
    /// Default is set to "outbox__$error".
    /// </summary>
    public string DatabaseErrorTableName { get; set; } = "outbox__error$";

    /// <summary>
    /// Gets the fully qualified name of the Outbox table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the Outbox table.</returns>
    public string GetQualifiedMsgTableName() => $@"{DatabaseSchemaName}.""{DatabaseMsgTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the delivery table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the delivery table.</returns>
    public string GetQualifiedDeliveryTableName() => $@"{DatabaseSchemaName}.""{DatabaseDeliveryTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the type table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the type table.</returns>
    public string GetQualifiedTypeTableName() => $@"{DatabaseSchemaName}.""{DatabaseTypeTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the offset table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the offset table.</returns>
    public string GetQualifiedOffsetTableName() => $@"{DatabaseSchemaName}.""{DatabaseOffsetTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the error table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the error table.</returns>
    public string GetQualifiedErrorTableName() => $@"{DatabaseSchemaName}.""{DatabaseErrorTableName}""";

    public string GetQualifiedTaskTableName() => $@"{DatabaseSchemaName}.""{DatabaseTaskTableName}""";
}


/// <summary>
/// Represents the settings for caching message types in the Outbox.
/// </summary>
public sealed class PgOutboxCacheSettings
{
    /// <summary>
    /// Gets or sets the duration for which message types are cached.
    /// Default is set to 1 day.
    /// </summary>
    public TimeSpan CacheTypeDuration { get; set; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Represents the settings for migrating the Outbox schema in PostgreSQL.
/// </summary>
public sealed class PgOutboxMigrationSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the migration should be executed as a background job.
    /// Default is set to true, meaning the migration will run as a job.
    /// </summary>
    public bool AsJob { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of days to move forward during the migration process.
    /// Default is set to 2 days.
    /// </summary>
    public int ForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets the interval at which the migration job will be executed.
    /// Default is set to every 4 hours, with a random additional delay of up to 59 minutes.
    /// </summary>
    public TimeSpan ExecutionInterval { get; set; } = TimeSpan
        .FromHours(4)
        .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));
}


/// <summary>
/// Represents the settings for cleaning up old Outbox messages and parts in PostgreSQL.
/// This class contains configuration options for how and when the cleanup should occur.
/// </summary>
public sealed class PgOutboxCleanupSettings
{
    /// <summary>
    /// Gets or sets a value indicating whether the cleanup should be executed as a background job.
    /// Default is set to false, meaning the cleanup will not run as a job.
    /// </summary>
    public bool AsJob { get; set; } = true;

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
}
