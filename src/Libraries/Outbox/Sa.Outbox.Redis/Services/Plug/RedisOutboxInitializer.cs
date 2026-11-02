using Sa.Outbox.PlugServices;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// One-time provider warm-up: <c>PING</c> on the provider's own database, so an unavailable Redis
/// fails a known startup step instead of the first delivery tick.
/// </summary>
internal sealed class RedisOutboxInitializer(IDatabase database) : IOutboxProviderInitializer
{
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => await database.PingAsync().ConfigureAwait(false);
}
