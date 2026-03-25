using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

public sealed record StorageOptions
{
    public string SchemaName { get; set; } = "public";
    public string TableName { get; set; } = "files";
    public string StorageType { get; set; } = "pg";
    public bool IsReadOnly { get; set; }
}

public sealed class CleanupOptions
{
    public int ExpireDays { get; set; } = 365 * 3;
}

public sealed class PartOptions
{
    public int MigrationScheduleForwardDays { get; set; } = 2;
    public PgPartBy PgPartBy { get; set; } = PgPartBy.Day;
}

public sealed class PostgresFileStorageOptions
{
    public StorageOptions StorageOptions { get; } = new();
    public PartOptions PartOptions { get; } = new();
    public CleanupOptions CleanupOptions { get; } = new();
    public string ScopeName { get; set; } = "share";
}
