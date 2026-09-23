using Microsoft.Extensions.Configuration;
using Sa.Data.PostgreSql;

namespace Sa.Configuration.PostgreSql;


/// <summary>
/// Configuration provider that loads settings from a PostgreSQL database.
/// </summary>
public sealed class DatabaseConfigurationProvider(PostgreSqlConfigurationOptions options)
    : ConfigurationProvider
{

    private readonly Lock @lock = new();


    /// <summary>
    /// Loads configuration from PostgreSQL database.
    /// </summary>
    public override void Load()
    {
        PgRetryStrategy
            .ExecuteWithRetry(fun: async _ => await LoadAsync())
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Asynchronously loads configuration from PostgreSQL database.
    /// </summary>
    private async Task<int> LoadAsync()
    {
        try
        {
            var newData = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            await using var dataSource = IPgDataSource.Create(options.ConnectionString);

            var count = await dataSource.ExecuteReader(options.SelectSql, (reader, _) =>
            {
                string key = reader.GetString(0);
                string? val = reader.IsDBNull(1) ? null : reader.GetString(1);

                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                {
                    newData.TryAdd(key.Trim(), val.Trim());
                }

            }, cmd =>
            {
                if (options.Parameters.Count > 0)
                {
                    cmd.Parameters.Clear();
                    foreach (var parameter in options.Parameters)
                        cmd.Parameters.Add(parameter.Clone());
                }
            }, CancellationToken.None);

            SetData(newData);

            return count;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration from PostgreSQL.", ex);
        }
    }

    private void SetData(Dictionary<string, string?> newData)
    {
        lock (@lock)
        {
            Data = newData;
        }
    }
}
