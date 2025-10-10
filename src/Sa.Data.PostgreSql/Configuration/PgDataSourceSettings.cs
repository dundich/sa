namespace Sa.Data.PostgreSql;

public sealed class PgDataSourceSettings(string connectionString)
{
    public string ConnectionString { get; } = connectionString;
}
