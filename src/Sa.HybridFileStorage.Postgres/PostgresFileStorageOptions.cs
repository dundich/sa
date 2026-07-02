using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Configuration options for the PostgreSQL storage engine (schema, table, read-only mode).
/// </summary>
public sealed record StorageOptions
{
    /// <summary>
    /// Gets or sets the database schema name. Defaults to <c>"public"</c>.
    /// </summary>
    public string SchemaName { get; set; } = "public";

    /// <summary>
    /// Gets or sets the database table name for storing file metadata. Defaults to <c>"files"</c>.
    /// </summary>
    public string TableName { get; set; } = "files";

    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <c>"pg"</c>.
    /// </summary>
    public string StorageType { get; set; } = "pg";

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; set; }
}

/// <summary>
/// Configuration options for automatic cleanup of expired file records.
/// </summary>
public sealed class CleanupOptions
{
    /// <summary>
    /// Gets or sets the number of days after which file records are considered expired and eligible for cleanup. Defaults to 1095 (3 years).
    /// </summary>
    public int ExpireDays { get; set; } = 365 * 3;
}

/// <summary>
/// Configuration options for PostgreSQL table partitioning.
/// </summary>
public sealed class PartOptions
{
    /// <summary>
    /// Gets or sets the number of days in advance to generate the migration schedule for new partitions. Defaults to 2.
    /// </summary>
    public int MigrationScheduleForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets the partitioning granularity (day, month, or year). Defaults to <see cref="PgPartBy.Day"/>.
    /// </summary>
    public PgPartBy PgPartBy { get; set; } = PgPartBy.Day;

    /// <summary>
    /// Gets or sets the basket (container) name. Defaults to <c>"share"</c>.
    /// </summary>
    public string Basket { get; set; } = "share";
}

/// <summary>
/// Aggregates all configuration options for the PostgreSQL file storage provider.
/// </summary>
public sealed class PostgresFileStorageOptions
{
    /// <summary>
    /// Gets the storage-specific options (schema, table, type, read-only mode).
    /// </summary>
    public StorageOptions StorageOptions { get; } = new();

    /// <summary>
    /// Gets the partitioning options.
    /// </summary>
    public PartOptions PartOptions { get; } = new();

    /// <summary>
    /// Gets the cleanup options.
    /// </summary>
    public CleanupOptions CleanupOptions { get; } = new();
}
