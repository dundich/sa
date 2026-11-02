using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Validates the installed provider and exposes its descriptor. Registered as a singleton by
/// <c>AddOutboxProvider</c>; every check below fails the construction, i.e. the first resolution —
/// which the hosted validator forces at startup and the delivery job settings build re-checks
/// before the first tick.
/// </summary>
internal sealed class OutboxProviderRegistry : IOutboxProviderRegistry
{
    public OutboxProviderDescriptor Descriptor { get; }

    public OutboxProviderRegistry(
        IEnumerable<OutboxProviderRegistration> registrations,
        IServiceProvider services)
    {
        OutboxProviderRegistration[] registered = [.. registrations];

        if (registered.Length == 0)
        {
            throw new InvalidOperationException(
                "No outbox storage provider is registered. Add one of the provider packages " +
                "(for example AddSaOutboxUsingPostgreSql(...) or AddSaOutboxUsingRedis(...)) — " +
                "publishing and delivery both need an IOutboxBulkWriter from a provider.");
        }

        if (registered.Length > 1)
        {
            string names = string.Join(
                ", ",
                registered.Select(static r => r.Descriptor.Name).Distinct(StringComparer.Ordinal));

            throw new InvalidOperationException(
                $"Multiple outbox storage providers are registered ({names}). The Outbox supports one " +
                "provider per container: remove one of the provider registrations.");
        }

        Descriptor = registered[0].Descriptor;

        // Validate the *registration*, never by constructing the provider's services: building a
        // storage chain can open connections and load state, which must not happen while a
        // schedule's job settings are being built. IServiceProviderIsService answers "is there a
        // registration" without any side effect; the fallback is for a non-Microsoft container.
        IServiceProviderIsService? isService = services.GetService<IServiceProviderIsService>();

        bool IsRegistered(Type type) => isService is not null
            ? isService.IsService(type)
            : services.GetService(type) is not null;

        if (!IsRegistered(typeof(IOutboxBulkWriter)))
        {
            throw new InvalidOperationException(
                $"Outbox provider '{Descriptor.Name}' does not register an {nameof(IOutboxBulkWriter)}. " +
                "Call AddOutboxProvider(..., provider => provider.WithBulkWriter<T>()).");
        }

        // The delivery manager is required only when the application actually consumes: a
        // publish-only deployment has no consumer groups and no delivery to run.
        bool hasConsumerGroups = services.GetServices<DeliveryGroupSettingsRegistration>().Any();

        if (hasConsumerGroups && !IsRegistered(typeof(IOutboxDeliveryManager)))
        {
            throw new InvalidOperationException(
                $"Outbox provider '{Descriptor.Name}' does not register an {nameof(IOutboxDeliveryManager)}, " +
                "but the application registers consumer groups. Add it with " +
                "AddOutboxProvider(..., provider => provider.WithDeliveryManager<T>()) or remove the deliveries.");
        }

        if (Descriptor.Supports(OutboxProviderCapabilities.TenantDetection)
            && !IsRegistered(typeof(IOutboxTenantDetector)))
        {
            throw new InvalidOperationException(
                $"Outbox provider '{Descriptor.Name}' declares the " +
                $"{nameof(OutboxProviderCapabilities.TenantDetection)} capability but registers no " +
                $"{nameof(IOutboxTenantDetector)}. Register one or drop the capability flag.");
        }
    }
}
