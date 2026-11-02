using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Bindable form of the PostgreSQL Outbox root settings (<see cref="PgOutboxSettings"/>), read from
/// an <c>IConfiguration</c> section by
/// <see cref="PgOutboxOptionsExtensions.WithOutboxSettingsFromConfiguration"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the <b>scalar</b> surface is bindable: the schema, the six table names and fill factors, and
/// the migration/cleanup schedules. The column definitions (<c>Fields.*</c>) are deliberately not
/// here — they are raw <c>name TYPE …</c> SQL fragments that the library splices into DDL verbatim,
/// i.e. trusted developer input, not deployment configuration.
/// </para>
/// <para>
/// Every property is nullable and a missing section is a no-op, so the section can be added or
/// removed without touching the deployment: whatever it does not name keeps its code value.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// {
///   "Outbox": {
///     "Pg": {
///       "Schema": "messaging",
///       "Tables": {
///         "TaskQueue": { "TableName": "outbox", "FillFactor": 70 },
///         "Message":   { "TableName": "outbox__msg$" }
///       },
///       "Migration": { "ForwardDays": 3, "ExecutionInterval": "06:00:00" },
///       "Cleanup":   { "DropPartsAfterRetention": "30.00:00:00" }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class PgOutboxOptions
{
    /// <summary>The schema that owns all six tables (maps to <see cref="PgOutboxTableSettings.DatabaseSchemaName"/>).</summary>
    public string? Schema { get; set; }

    /// <summary>Creates a DEFAULT catch-all partition on every table (keeps rows with unmigrated keys).</summary>
    public bool? UseDefaultPartition { get; set; }

    /// <summary>Table names and fill factors. Omitted tables keep their code values.</summary>
    public PgOutboxTablesOptions? Tables { get; set; }

    /// <summary>Partition migration schedule.</summary>
    public PgOutboxMigrationOptions? Migration { get; set; }

    /// <summary>Old-partition cleanup schedule.</summary>
    public PgOutboxCleanupOptions? Cleanup { get; set; }

    /// <summary>Applies the non-null values to <paramref name="settings"/>.</summary>
    internal void ApplyTo(PgOutboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        PgOutboxTableSettings tables = settings.TableSettings;

        if (Schema is { } schema) tables.DatabaseSchemaName = schema;
        if (UseDefaultPartition is { } useDefault) tables.UseDefaultPartition = useDefault;

        if (Tables is { } configured)
        {
            Apply(configured.TaskQueue, n => tables.TaskQueue.TableName = n, f => tables.TaskQueue.FillFactor = f);
            Apply(configured.Message, n => tables.Message.TableName = n, f => tables.Message.FillFactor = f);
            Apply(configured.Delivery, n => tables.Delivery.TableName = n, f => tables.Delivery.FillFactor = f);
            Apply(configured.Error, n => tables.Error.TableName = n, f => tables.Error.FillFactor = f);
            Apply(configured.Type, n => tables.Type.TableName = n, f => { });
            Apply(configured.Offset, n => tables.Offset.TableName = n, f => { });
        }

        if (Migration is { } migration)
        {
            if (migration.AsBackgroundJob is { } background) settings.MigrationSettings.AsBackgroundJob = background;
            if (migration.ForwardDays is { } forwardDays) settings.MigrationSettings.ForwardDays = forwardDays;
            if (migration.ExecutionInterval is { } interval) settings.MigrationSettings.ExecutionInterval = interval;
        }

        if (Cleanup is { } cleanup)
        {
            if (cleanup.AsBackgroundJob is { } background) settings.CleanupSettings.AsBackgroundJob = background;
            if (cleanup.DropPartsAfterRetention is { } retention) settings.CleanupSettings.DropPartsAfterRetention = retention;
            if (cleanup.CleanupMode is { } mode) settings.CleanupSettings.CleanupMode = mode;
            if (cleanup.ArchiveSchemaName is { } archiveSchema) settings.CleanupSettings.ArchiveSchemaName = archiveSchema;
            if (cleanup.ExecutionInterval is { } interval) settings.CleanupSettings.ExecutionInterval = interval;
        }
    }

    /// <summary>
    /// Applies one table's options. The two tables without a fill factor (<c>__type$</c> and
    /// <c>__offset$</c>) pass an empty setter.
    /// </summary>
    private static void Apply(
        PgOutboxTableOptions? options,
        Action<string> setTableName,
        Action<int> setFillFactor)
    {
        if (options is null) return;

        if (options.TableName is { } tableName) setTableName(tableName);
        if (options.FillFactor is { } fillFactor) setFillFactor(fillFactor);
    }
}

/// <summary>The six table names / fill factors of <see cref="PgOutboxTableSettings"/>.</summary>
public sealed class PgOutboxTablesOptions
{
    /// <summary>The task queue table (read-write, <c>SKIP LOCKED</c>). Default name is the base table name.</summary>
    public PgOutboxTableOptions? TaskQueue { get; set; }

    /// <summary>The source messages table (append-only, <c>COPY</c> target).</summary>
    public PgOutboxTableOptions? Message { get; set; }

    /// <summary>The delivery history table.</summary>
    public PgOutboxTableOptions? Delivery { get; set; }

    /// <summary>The permanent errors table.</summary>
    public PgOutboxTableOptions? Error { get; set; }

    /// <summary>The message-type registry table (no fill factor).</summary>
    public PgOutboxTableOptions? Type { get; set; }

    /// <summary>The consumer-group offsets table (no fill factor).</summary>
    public PgOutboxTableOptions? Offset { get; set; }
}

/// <summary>One table's name and fill factor. Both optional.</summary>
public sealed class PgOutboxTableOptions
{
    /// <summary>The table name as it appears in DDL. Omitted — the code value stays.</summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The fill factor, 10–100 (a PostgreSQL constraint). Omitted — the code value stays.
    /// A value outside the range fails at startup.
    /// </summary>
    public int? FillFactor { get; set; }
}

/// <summary>Partition migration schedule (binds the scalar part of <see cref="PgOutboxMigrationSettings"/>).</summary>
public sealed class PgOutboxMigrationOptions
{
    /// <summary>Run the migration as a background job (otherwise it is only resolvable for manual runs).</summary>
    public bool? AsBackgroundJob { get; set; }

    /// <summary>How many days ahead the daily partitions are guaranteed to exist.</summary>
    public int? ForwardDays { get; set; }

    /// <summary>How often the migration job runs.</summary>
    public TimeSpan? ExecutionInterval { get; set; }
}

/// <summary>Old-partition cleanup schedule (binds the scalar part of <see cref="PgOutboxCleanupSettings"/>).</summary>
public sealed class PgOutboxCleanupOptions
{
    /// <summary>Run the cleanup as a background job (otherwise it is only resolvable for manual runs).</summary>
    public bool? AsBackgroundJob { get; set; }

    /// <summary>How long a partition is kept before it is dropped (or archived).</summary>
    public TimeSpan? DropPartsAfterRetention { get; set; }

    /// <summary>Whether expired parts are dropped or moved to <see cref="ArchiveSchemaName"/>.</summary>
    public PartCleanupMode? CleanupMode { get; set; }

    /// <summary>The schema archived parts are moved into when <see cref="CleanupMode"/> is <see cref="PartCleanupMode.Archive"/>.</summary>
    public string? ArchiveSchemaName { get; set; }

    /// <summary>How often the cleanup job runs.</summary>
    public TimeSpan? ExecutionInterval { get; set; }
}
