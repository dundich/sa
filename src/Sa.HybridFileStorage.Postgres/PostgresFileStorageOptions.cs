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
    /// Must be a bare SQL identifier — quoting it (<c>"\"files\""</c>) is rejected rather than
    /// silently trimmed, because the same value is used for both the DDL and the queries.
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
    public string Basket { get; set; } = StorageNaming.DefaultBasket;

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
    /// Validates the current configuration and throws an <see cref="ArgumentException"/> describing
    /// the first offending property.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any property is invalid.</exception>
    /// <remarks>
    /// Called through the standard options pipeline (<c>IValidateOptions</c>) when the options
    /// materialise — the first read (storage, DDL or schema resolver), or host start with
    /// <c>ValidateOnStart()</c>. Without it, an over-long or illegal <see cref="TableName"/>
    /// reaches the DDL and the <c>Sanitize</c> rewrite in the provider produces a *different*
    /// name than the one registered, and the mismatch only surfaces as <c>relation does not
    /// exist</c> on the first upload. Callers holding their own instance may invoke it directly.
    /// </remarks>
    public void Validate()
    {
        StorageNaming.RequireIdentifier(TableName, nameof(TableName));
        StorageNaming.RequireStorageType(StorageType, nameof(StorageType));
        StorageNaming.ValidateBasket(Basket, nameof(Basket));

        if (SchemaName is not null)
        {
            // Only the first schema of a search_path list is the effective one for table resolution,
            // so a comma here would silently register the table under a name nothing can query.
            StorageNaming.RequireIdentifier(SchemaName, nameof(SchemaName));
        }

        if (ExpireDays < 1)
        {
            throw new ArgumentException(
                $"ExpireDays must be greater than zero, but was {ExpireDays}.", nameof(ExpireDays));
        }

        if (MigrationScheduleForwardDays < 1)
        {
            throw new ArgumentException(
                $"MigrationScheduleForwardDays must be greater than zero, but was {MigrationScheduleForwardDays}.",
                nameof(MigrationScheduleForwardDays));
        }
    }

    /// <summary>
    /// Creates a copy of these options with identical values.
    /// </summary>
    internal PostgresFileStorageOptions Copy()
    {
        PostgresFileStorageOptions copy = new();
        CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// Writes every property of these options into <paramref name="target"/> — used to seed
    /// the pipeline's fresh instance from an explicit instance without touching the caller's object.
    /// </summary>
    internal void CopyTo(PostgresFileStorageOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.SchemaName = SchemaName;
        target.TableName = TableName;
        target.StorageType = StorageType;
        target.IsReadOnly = IsReadOnly;
        target.Basket = Basket;
        target.ExpireDays = ExpireDays;
        target.MigrationScheduleForwardDays = MigrationScheduleForwardDays;
        target.PgPartBy = PgPartBy;
    }
}
