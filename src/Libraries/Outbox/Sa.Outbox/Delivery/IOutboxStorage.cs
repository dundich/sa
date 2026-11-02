using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Delivery;

internal interface IOutboxStorage
{
    IOutboxBulkWriter Writer(string providerKey);

    IOutboxDeliveryManager DeliveryManager(string providerKey);

    IOutboxTenantDetector? TenantDetector(string providerKey);

    Sa.Outbox.Partitional.TenantSettings TenantSettings(string providerKey);
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
        // Unkeyed first: a detector registered without a key is an explicit global override (the
        // pre-multi-provider way to replace the provider's detector). Without one, the provider's
        // own keyed detector is used.
        => services.GetService<IOutboxTenantDetector>()
           ?? services.GetKeyedService<IOutboxTenantDetector>(providerKey);

    public Sa.Outbox.Partitional.TenantSettings TenantSettings(string providerKey)
        => services.GetKeyedService<Sa.Outbox.Partitional.TenantSettings>(providerKey)
           ?? services.GetRequiredService<Sa.Outbox.Partitional.TenantSettings>();
}
