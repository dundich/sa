using Sa.Outbox.PlugServices;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// Cheap liveness check for the Redis provider: <c>PING</c> on the provider's own database. The
/// aggregate stamps the provider key; the reported latency is the ping round trip.
/// </summary>
internal sealed class RedisOutboxHealth(IDatabase database) : IOutboxProviderHealth
{
    public async ValueTask<OutboxProviderHealthReport> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        TimeSpan latency = await database.PingAsync().ConfigureAwait(false);

        return new OutboxProviderHealthReport(string.Empty, Healthy: true, Latency: latency);
    }
}
