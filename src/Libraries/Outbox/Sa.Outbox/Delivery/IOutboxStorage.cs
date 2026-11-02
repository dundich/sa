using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Delivery;

internal interface IOutboxStorage
{
    IOutboxBulkWriter Writer(string providerKey);

    IOutboxDeliveryManager DeliveryManager(string providerKey);

    IOutboxTenantDetector? TenantDetector(string providerKey);
}

/// <summary>
/// Keyed-first resolution of the provider contracts with an unkeyed fallback: a single-provider
/// application (or a test double registered without a key) keeps working while providers migrate to
/// keyed registrations, and once the keyed registration exists it wins.
/// </summary>
internal sealed class OutboxStorage(IServiceProvider services) : IOutboxStorage
{
    public IOutboxBulkWriter Writer(string providerKey)
        => services.GetKeyedService<IOutboxBulkWriter>(providerKey)
           ?? services.GetRequiredService<IOutboxBulkWriter>();

    public IOutboxDeliveryManager DeliveryManager(string providerKey)
        => services.GetKeyedService<IOutboxDeliveryManager>(providerKey)
           ?? services.GetRequiredService<IOutboxDeliveryManager>();

    public IOutboxTenantDetector? TenantDetector(string providerKey)
        => services.GetKeyedService<IOutboxTenantDetector>(providerKey)
           ?? services.GetService<IOutboxTenantDetector>();
}
