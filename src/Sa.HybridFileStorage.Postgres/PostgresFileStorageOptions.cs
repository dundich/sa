using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Configuration options for the PostgreSQL file storage provider.
/// </summary>
public sealed record PostgresFileStorageOptions
{
    /// <summary>
    /// Gets or sets the database schema name.
    /// When <c>null</c> (default), the schema is auto-detected from the connection's
    /// <c>Search Path</c> at startup, falling back to <c>"public"</c>.
    /// </summary>
    public string? SchemaName { get; set; }

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

    /// <summary>
    /// Gets or sets the basket (container) name. Defaults to <c>"share"</c>.
    /// </summary>
    public string Basket { get; set; } = "share";

    /// <summary>
    /// Gets or sets the number of days after which file records are considered expired and eligible for cleanup. Defaults to 3 years (<c>1095</c>).
    /// </summary>
    public int ExpireDays { get; set; } = 365 * 3;

    /// <summary>
    /// Gets or sets the number of days in advance to generate the migration schedule for new partitions. Defaults to 2.
    /// </summary>
    public int MigrationScheduleForwardDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets the partitioning granularity (day, month, or year). Defaults to <see cref="PgPartBy.Day"/>.
    /// </summary>
    public PgPartBy PgPartBy { get; set; } = PgPartBy.Day;

    /// <summary>
    /// Creates a copy of these options with identical values.
    /// </summary>
    internal PostgresFileStorageOptions Copy()
    {
        return new()
        {
            SchemaName = SchemaName,
            TableName = TableName,
            StorageType = StorageType,
            IsReadOnly = IsReadOnly,
            Basket = Basket,
            ExpireDays = ExpireDays,
            MigrationScheduleForwardDays = MigrationScheduleForwardDays,
            PgPartBy = PgPartBy,
        };
    }
}
