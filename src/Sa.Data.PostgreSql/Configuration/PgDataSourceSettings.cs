using Npgsql;

namespace Sa.Data.PostgreSql;

internal sealed class PgDataSourceSettings(string connectionString)
{
    private string? _searchPath;

    public string ConnectionString => connectionString;

    public string GetSearchPath()
    {
        if (_searchPath is not null)
            return _searchPath;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return _searchPath = builder.SearchPath ?? "public";
        }
        catch
        {
            return _searchPath = "public";
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string cannot be null or empty.");
        }
    }
}
