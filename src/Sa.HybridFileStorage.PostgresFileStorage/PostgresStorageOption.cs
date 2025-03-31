namespace Sa.HybridFileStorage.PostgresFileStorage;

public class PostgresStorageOption
{
    public string SchemaName { get; set; } = "public";
    public string TableName { get; set; } = "files";
    public string StorageType { get; set; } = "pg";
    public bool? IsReadOnly { get; set; }
    public int ExpireDays { get; set; } = 365 * 3;
    public int MigrationScheduleForwardDays { get; set; } = 2;
}
