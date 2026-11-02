using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// Registration-level checks of the provider plug point for the Redis package: the three storage
/// contracts and the provider registry must be registered by <c>AddSaOutboxUsingRedis</c> alone.
/// No Redis server is needed — nothing is resolved here.
/// </summary>
public class OutboxProviderRegistrationTests
{
    [Fact]
    public void AddSaOutboxUsingRedis_RegistersProviderContractsAndRegistry()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis();

        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxBulkWriter));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxDeliveryManager));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxTenantDetector));
        Assert.Contains(services, d => d.ServiceType == typeof(IOutboxProviderRegistry));
    }
}
