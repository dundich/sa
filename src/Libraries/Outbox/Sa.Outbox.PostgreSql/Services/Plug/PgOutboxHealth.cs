using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.PostgreSql.Services.Plug;

/// <summary>
/// Cheap liveness check for the PostgreSQL provider: <c>SELECT 1</c> on the provider's own data
/// source. The aggregate stamps the provider key and measures the latency.
/// </summary>
internal sealed class PgOutboxHealth(IPgDataSource dataSource) : IOutboxProviderHealth
{
    public async ValueTask<OutboxProviderHealthReport> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        int result = await dataSource
            .ExecuteScalarTyped<int>("SELECT 1", initCommand: null, cancellationToken)
            .ConfigureAwait(false);

        return result == 1
            ? new OutboxProviderHealthReport(string.Empty, Healthy: true)
            : new OutboxProviderHealthReport(string.Empty, Healthy: false, Reason: $"SELECT 1 returned {result}.");
    }
}
