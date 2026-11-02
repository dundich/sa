using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Validates the installation of one provider and exposes its descriptor. Registered keyed by
/// provider key; every check below fails the construction, i.e. the first resolution — which the
/// hosted validator forces for every registered key at startup.
/// </summary>
internal sealed class OutboxProviderRegistry : IOutboxProviderRegistry
{
    public OutboxProviderDescriptor Descriptor { get; }

    public OutboxProviderRegistry(string providerKey, IServiceProvider services)
    {
        OutboxProviderRegistration[] registered =
        [
            .. services.GetServices<OutboxProviderRegistration>()
                .Where(r => string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal)),
        ];

        if (registered.Length == 0)
        {
            throw new InvalidOperationException(
                $"No outbox storage provider is registered under the key '{providerKey}'. " +
                "Add one of the provider packages with that key.");
        }

        if (registered.Length > 1)
        {
            string names = string.Join(
                ", ",
                registered.Select(static r => r.Descriptor.Name).Distinct(StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"Multiple outbox providers are registered under the key '{providerKey}' ({names}). " +
                "One provider per key: remove one of the registrations or use a different key.");
        }

        OutboxProviderRegistration registration = registered[0];
        Descriptor = registration.Descriptor;

        // Presence checks use the registration marker, never service construction: building a
        // provider's chain can open connections and load state, and the registry is also read while
        // delivery job settings are being built.
        bool hasConsumerGroups = services.GetServices<DeliveryGroupSettingsRegistration>()
            .Any(g => string.Equals(g.ProviderKey, providerKey, StringComparison.Ordinal));

        if (hasConsumerGroups && !registration.HasDeliveryManager)
        {
            throw new InvalidOperationException(
                $"Outbox provider '{Descriptor.Name}' (key '{providerKey}') does not register an " +
                $"{nameof(IOutboxDeliveryManager)}, but consumer groups are registered for it. " +
                "Add it with AddOutboxProvider(..., provider => provider.WithDeliveryManager<T>()) " +
                "or remove the deliveries.");
        }

        if (Descriptor.Supports(OutboxProviderCapabilities.TenantDetection)
            && !registration.HasTenantDetector)
        {
            throw new InvalidOperationException(
                $"Outbox provider '{Descriptor.Name}' (key '{providerKey}') declares the " +
                $"{nameof(OutboxProviderCapabilities.TenantDetection)} capability but registers no " +
                $"{nameof(IOutboxTenantDetector)}. Register one or drop the capability flag.");
        }
    }
}
