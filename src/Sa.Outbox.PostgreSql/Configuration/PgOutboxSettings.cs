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
    public string DatabaseSchemaName { get; set; } = PgOutboxTableSettingsExtensions.DatabaseSchemaName;

    /// <summary>
    /// Default is set to "outbox".
    /// </summary>
    public string DatabaseTableName { get; set; }
        = PgOutboxTableSettingsExtensions.DatabaseTableName;

    /// <summary>
    /// Gets or sets the name of the Outbox Messages table.
    /// Default is set to "outbox__$msg".
    /// </summary>
    public string DatabaseMsgTableName { get; set; }
        = $"{PgOutboxTableSettingsExtensions.DatabaseTableName}{PgOutboxTableSettingsExtensions.MsgSuffix}";

    /// <summary>
    /// Gets or sets the name of the delivery table.
    /// Default is set to "outbox__log$".
    /// </summary>
    public string DatabaseDeliveryTableName { get; set; }
        = $"{PgOutboxTableSettingsExtensions.DatabaseTableName}{PgOutboxTableSettingsExtensions.DeliverySuffix}";

    /// <summary>
    /// Gets or sets the name of the type table.
    /// Default is set to "outbox__type$".
    /// </summary>
    public string DatabaseTypeTableName { get; set; }
        = $"{PgOutboxTableSettingsExtensions.DatabaseTableName}{PgOutboxTableSettingsExtensions.TypeSuffix}";

    /// <summary>
    /// Gets or sets the offset for receiving group messages.
    /// Default is set to "outbox__offset$".
    /// </summary>
    public string DatabaseOffsetTableName { get; set; }
        = $"{PgOutboxTableSettingsExtensions.DatabaseTableName}{PgOutboxTableSettingsExtensions.OffsetSuffix}";

    /// <summary>
    /// Gets or sets the name of the error table.
    /// Default is set to "outbox__error$".
    /// </summary>
    public string DatabaseErrorTableName { get; set; }
        = $"{PgOutboxTableSettingsExtensions.DatabaseTableName}{PgOutboxTableSettingsExtensions.ErrorSuffix}";
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



public static class PgOutboxTableSettingsExtensions
{

    public const string DatabaseSchemaName = "public";
    public const string DatabaseTableName = "outbox";

    public const string MsgSuffix = "__msg$";
    public const string DeliverySuffix = "__log$";
    public const string TypeSuffix = "__type$";
    public const string OffsetSuffix = "__offset$";
    public const string ErrorSuffix = "__error$";


    /// <summary>
    /// Gets the fully qualified name of the Outbox table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the Outbox table.</returns>
    public static string GetQualifiedMsgTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseMsgTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the delivery table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the delivery table.</returns>
    public static string GetQualifiedDeliveryTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseDeliveryTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the type table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the type table.</returns>
    public static string GetQualifiedTypeTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseTypeTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the offset table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the offset table.</returns>
    public static string GetQualifiedOffsetTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseOffsetTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the error table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the error table.</returns>
    public static string GetQualifiedErrorTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseErrorTableName}""";

    /// <summary>
    /// Gets the fully qualified name of the task table, including the schema.
    /// </summary>
    /// <returns>The qualified name of the task table.</returns>
    public static string GetQualifiedTaskTableName(this PgOutboxTableSettings settings)
        => $@"{settings.DatabaseSchemaName}.""{settings.DatabaseTableName}""";

    /// <summary>
    /// Configures all table names based on a single base table name.
    /// </summary>
    /// <param name="settings">The settings instance.</param>
    /// <param name="baseTableName">Base name for all tables.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxTableSettings UseBaseTableName(
        this PgOutboxTableSettings settings,
        string baseTableName)
    {
        if (string.IsNullOrWhiteSpace(baseTableName))
            throw new ArgumentException("Base table name cannot be null or empty", nameof(baseTableName));

        settings.DatabaseTableName = baseTableName;
        settings.DatabaseMsgTableName = $"{baseTableName}{MsgSuffix}";
        settings.DatabaseDeliveryTableName = $"{baseTableName}{DeliverySuffix}";
        settings.DatabaseTypeTableName = $"{baseTableName}{TypeSuffix}";
        settings.DatabaseOffsetTableName = $"{baseTableName}{OffsetSuffix}";
        settings.DatabaseErrorTableName = $"{baseTableName}{ErrorSuffix}";

        return settings;
    }

    /// <summary>
    /// Configures all table names based on a single base table name with custom schema.
    /// </summary>
    public static PgOutboxTableSettings UseBaseTableName(
        this PgOutboxTableSettings settings,
        string baseTableName,
        string schemaName)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new ArgumentException("Schema name cannot be null or empty", nameof(schemaName));

        settings.UseBaseTableName(baseTableName);
        settings.DatabaseSchemaName = schemaName;

        return settings;
    }

    /// <summary>
    /// Sets custom schema name for all tables.
    /// </summary>
    public static PgOutboxTableSettings WithSchema(
        this PgOutboxTableSettings settings,
        string schemaName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(schemaName);
        settings.DatabaseSchemaName = schemaName;
        return settings;
    }

    /// <summary>
    /// Sets custom message table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithMsgTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.DatabaseMsgTableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom delivery table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithDeliveryTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.DatabaseDeliveryTableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom type table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithTypeTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.DatabaseTypeTableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom offset table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithOffsetTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.DatabaseOffsetTableName = tableName;
        return settings;
    }

    /// <summary>
    /// Sets custom error table name (overrides auto-generated name).
    /// </summary>
    public static PgOutboxTableSettings WithErrorTableName(
        this PgOutboxTableSettings settings,
        string tableName)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tableName);
        settings.DatabaseErrorTableName = tableName;
        return settings;
    }
}



public static class PgOutboxMigrationSettingsExtensions
{
    /// <summary>
    /// Configures migration to run as a background job.
    /// </summary>
    /// <param name="settings">The migration settings.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxMigrationSettings RunAsJob(this PgOutboxMigrationSettings settings)
    {
        settings.AsJob = true;
        return settings;
    }


    /// <summary>
    /// Sets the execution interval for the migration job.
    /// </summary>
    /// <param name="settings">The migration settings.</param>
    /// <param name="interval">The execution interval.</param>
    /// <returns>The configured settings instance.</returns>
    public static PgOutboxMigrationSettings WithExecutionInterval(
        this PgOutboxMigrationSettings settings,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentException("Execution interval must be positive", nameof(interval));

        settings.ExecutionInterval = interval;
        return settings;
    }

    /// <summary>
    /// Configures migration with recommended production settings.
    /// </summary>
    public static PgOutboxMigrationSettings UseProductionSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsJob = true;
        settings.ForwardDays = 7; // Longer forward window for production
        settings.ExecutionInterval = TimeSpan.FromHours(6)
            .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

        return settings;
    }

    /// <summary>
    /// Configures migration with recommended development settings.
    /// </summary>
    public static PgOutboxMigrationSettings UseDevelopmentSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsJob = true;
        settings.ForwardDays = 1;
        settings.ExecutionInterval = TimeSpan.FromHours(1);

        return settings;
    }

    /// <summary>
    /// Configures migration for testing purposes.
    /// </summary>
    public static PgOutboxMigrationSettings UseTestSettings(
        this PgOutboxMigrationSettings settings)
    {
        settings.AsJob = false;
        settings.ForwardDays = 0; // No forward movement in tests
        settings.ExecutionInterval = TimeSpan.Zero; // No interval for tests

        return settings;
    }
}


public static class PgOutboxCleanupSettingsExtensions
{
    /// <summary>
    /// Configures cleanup to run as a background job.
    /// </summary>
    public static PgOutboxCleanupSettings RunAsJob(this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = true;
        return settings;
    }

    /// <summary>
    /// Configures cleanup to run immediately (not as a background job).
    /// </summary>
    public static PgOutboxCleanupSettings RunImmediately(this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = false;
        return settings;
    }

    /// <summary>
    /// Sets whether cleanup should run as a background job.
    /// </summary>
    public static PgOutboxCleanupSettings SetAsJob(
        this PgOutboxCleanupSettings settings,
        bool asJob)
    {
        settings.AsJob = asJob;
        return settings;
    }

    /// <summary>
    /// Sets the retention period for dropping old parts.
    /// </summary>
    public static PgOutboxCleanupSettings WithRetentionPeriod(
        this PgOutboxCleanupSettings settings,
        TimeSpan retentionPeriod)
    {
        if (retentionPeriod <= TimeSpan.Zero)
            throw new ArgumentException("Retention period must be positive", nameof(retentionPeriod));

        settings.DropPartsAfterRetention = retentionPeriod;
        return settings;
    }

    /// <summary>
    /// Sets the retention period in days.
    /// </summary>
    public static PgOutboxCleanupSettings WithRetentionDays(
        this PgOutboxCleanupSettings settings,
        int days)
    {
        if (days <= 0)
            throw new ArgumentException("Retention days must be positive", nameof(days));

        settings.DropPartsAfterRetention = TimeSpan.FromDays(days);
        return settings;
    }

    /// <summary>
    /// Configures cleanup for testing purposes (no actual cleanup).
    /// </summary>
    public static PgOutboxCleanupSettings UseTestSettings(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = false;
        settings.DropPartsAfterRetention = TimeSpan.MaxValue; // Never drop in tests
        settings.ExecutionInterval = TimeSpan.Zero;

        return settings;
    }

    /// <summary>
    /// Configures cleanup to never drop old parts (infinite retention).
    /// </summary>
    public static PgOutboxCleanupSettings KeepForever(
        this PgOutboxCleanupSettings settings)
    {
        settings.DropPartsAfterRetention = TimeSpan.MaxValue;
        return settings;
    }

    /// <summary>
    /// Configures cleanup with aggressive settings (frequent cleanup, short retention).
    /// </summary>
    public static PgOutboxCleanupSettings UseAggressiveCleanup(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(7); // Keep only 7 days
        settings.ExecutionInterval = TimeSpan.FromHours(1); // Clean every hour

        return settings;
    }

    /// <summary>
    /// Configures cleanup with conservative settings (less frequent, longer retention).
    /// </summary>
    public static PgOutboxCleanupSettings UseConservativeCleanup(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(90); // Keep 90 days
        settings.ExecutionInterval = TimeSpan.FromDays(1); // Clean daily

        return settings;
    }

    /// <summary>
    /// Configures cleanup with recommended production settings.
    /// </summary>
    public static PgOutboxCleanupSettings UseProductionSettings(
        this PgOutboxCleanupSettings settings)
    {
        settings.AsJob = true;
        settings.DropPartsAfterRetention = TimeSpan.FromDays(30); // 30 days retention
        settings.ExecutionInterval = TimeSpan.FromHours(4)
            .Add(TimeSpan.FromMinutes(Random.Shared.Next(1, 59)));

        return settings;
    }
}
