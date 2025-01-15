namespace Sa.Data.PostgreSql;

public class PgDataSourceSettings(string connectionString)
{
    public string ConnectionString { get; } = connectionString;
}
