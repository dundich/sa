using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.PostgresFileStorage;

public class StorageOptions
{
    public string SchemaName { get; set; } = "public";
    public string TableName { get; set; } = "files";
    public string StorageType { get; set; } = "pg";
    public bool IsReadOnly { get; set; } = false;
}

public class CleanupOptions
{
    public int ExpireDays { get; set; } = 365 * 3;
}

public class PartOptions
{
    public int MigrationScheduleForwardDays { get; set; } = 2;
    public PgPartBy PgPartBy { get; set; } = PgPartBy.Day;
}

public class PostgresFileStorageOptions
{
    public StorageOptions StorageOptions { get; } = new();
    public PartOptions PartOptions { get; } = new();
    public CleanupOptions CleanupOptions { get; } = new();
}
