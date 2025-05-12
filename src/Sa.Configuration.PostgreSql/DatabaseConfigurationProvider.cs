namespace Sa.Configuration.PostgreSql;

using Microsoft.Extensions.Configuration;
using Sa.Data.PostgreSql;

public class DatabaseConfigurationProvider(PostgreSqlConfigurationOptions options) : ConfigurationProvider
{
    public override void Load()
    {
        LoadAsync(options).GetAwaiter().GetResult();
    }

    private async Task LoadAsync(PostgreSqlConfigurationOptions options)
    {
        try
        {
            using var dataSource = new PgDataSource(new(options.ConnectionString));

            await dataSource.ExecuteReader(options.SelectSql, (reader, _) =>
            {
                string key = reader.GetString(0);
                string? val = reader.IsDBNull(1) ? null : reader.GetString(1);

                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                {
                    Set(key.Trim(), val.Trim());
                }

            }, options.Parameters ?? [], CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration from PostgreSQL.", ex);
        }
    }
}
