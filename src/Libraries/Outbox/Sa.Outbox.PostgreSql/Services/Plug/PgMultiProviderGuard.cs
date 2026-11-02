using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSql.Services.Plug;

/// <summary>
/// Fails the host start when two PostgreSQL outbox providers point at the same tables: both would
/// consume the same <c>task$</c> rows, i.e. deliver every message twice.
/// </summary>
/// <remarks>
/// <para>
/// A provider's layout is its database identity (host, port, database), the schema and the table
/// names. Two providers with distinct schemas (the recommended multi-provider setup), or with a
/// distinct database, are independent and pass. Resolving the data source and the table settings
/// opens no connection: the connection string and the settings are local values.
/// </para>
/// <para>
/// This guard exists because the multi-provider feature makes the mistake possible, and because an
/// existing deployment adding a second provider is exactly the migration where it is likely.
/// </para>
/// </remarks>
internal sealed partial class PgMultiProviderGuard(
    IEnumerable<PgProviderLayout> layouts,
    IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (PgProviderLayout layout in layouts)
        {
            IPgDataSource dataSource = PgDataSourceResolver.Resolve(services, layout.ProviderKey);
            PgOutboxTableSettings tables = services.GetRequiredKeyedService<PgOutboxTableSettings>(layout.ProviderKey);

            string fingerprint = string.Join(
                '|',
                DatabaseIdentity(dataSource.GetConnectionString()),
                tables.DatabaseSchemaName,
                tables.TaskQueue.TableName,
                tables.Message.TableName,
                tables.Delivery.TableName);

            if (seen.TryGetValue(fingerprint, out string? other))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL outbox providers '{other}' and '{layout.ProviderKey}' point at the same " +
                    $"tables ({tables.DatabaseSchemaName}.{tables.TaskQueue.TableName}). Both would consume " +
                    "the same rows and deliver every message twice. Give one of them its own schema or " +
                    "table names via WithOutboxSettings.");
            }

            seen[fingerprint] = layout.ProviderKey;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string DatabaseIdentity(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"Host={builder.Host};Port={builder.Port};Database={builder.Database}";
    }
}
