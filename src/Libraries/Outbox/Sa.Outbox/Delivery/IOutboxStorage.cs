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
/// Keyed-first resolution of the provider storage contracts. The unkeyed fallback is limited to the
/// default provider key, so a single-provider application (or a test double registered without a
/// key) keeps working while a non-default key can never resolve another provider's storage. Tenant
/// settings are the exception: they intentionally fall back to the default provider's shared list.
/// </summary>
internal sealed class OutboxStorage(IServiceProvider services) : IOutboxStorage
{
    public IOutboxBulkWriter Writer(string providerKey)
        => OutboxProviderResolution.Required<IOutboxBulkWriter>(services, providerKey);

    public IOutboxDeliveryManager DeliveryManager(string providerKey)
        => OutboxProviderResolution.Required<IOutboxDeliveryManager>(services, providerKey);

    public IOutboxTenantDetector? TenantDetector(string providerKey)
        // Unkeyed first: a detector registered without a key is an explicit global override (the
        // pre-multi-provider way to replace the provider's detector). Without one, the provider's
        // own keyed detector is used.
        => services.GetService<IOutboxTenantDetector>()
           ?? services.GetKeyedService<IOutboxTenantDetector>(providerKey);

    public Sa.Outbox.Partitional.TenantSettings TenantSettings(string providerKey)
        // Tenants deliberately keep the shared fallback: a provider without its own settings uses
        // the default provider's list (the documented per-provider tenant model).
        => services.GetKeyedService<Sa.Outbox.Partitional.TenantSettings>(providerKey)
           ?? services.GetRequiredService<Sa.Outbox.Partitional.TenantSettings>();
}
