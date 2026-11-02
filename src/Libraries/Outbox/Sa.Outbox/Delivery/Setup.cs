using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
using Sa.Outbox.PlugServices;
using Sa.Schedule;

namespace Sa.Outbox.Delivery;

internal static class Setup
{
    public static IServiceCollection AddOutboxDelivery(
        this IServiceCollection services,
        Action<IDeliveryBuilder>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        services.AddMessagesMetadata();

        services.TryAddSingleton<FilterFactory>();

        services.TryAddSingleton<IOutboxContextFactory, OutboxContextFactory>();

        services.TryAddSingleton<IDeliveryBatcher, DeliveryBatcher>();

        // Empty-queue back-off: the strategy decides the wait between polls, the registry holds
        // one idle state per consumer group shared by the job and its adaptive timing.
        services.TryAddSingleton<IEmptyQueueWaitStrategy, ExponentialIdleWaitStrategy>();
        services.TryAddSingleton<IOutboxIdleStateRegistry, OutboxIdleStateRegistry>();

        // looper - job processor
        services.TryAddSingleton<IDeliveryProcessor, DeliveryProcessor>();
        // sender - sending to scope consumer
        services.TryAddSingleton<IDeliveryCourier, DeliveryCourier>();

        // support - messaging to each tenant
        services.AddOutboxPartitional();

        // Delivery-group options provider: the base no-op — a provider package replaces it
        // with its own implementation. Resolved from the delivery job body, so with the base
        // package only, a group is shaped by OutboxDefaults, the section convention below and
        // the user configure callback.
        services.AddDefaultDeliveryGroupOptionsProvider();

        // The configuration-section convention for consumer groups: {prefix}:{group} is bound when
        // the group was registered without an explicit configurationKey. Replaced by
        // IOutboxBuilder.WithDeliveryConfigurationSection (null prefix disables it).
        services.TryAddSingleton(new OutboxDeliveryConfiguration());

        services.TryAddSingleton<IDeliveryTenant, DeliveryTenant>();

        // Provider contracts are resolved by provider key through this router: the delivery
        // pipeline itself stays provider-agnostic and singletons stay unkeyed.
        services.TryAddSingleton<IOutboxStorage, OutboxStorage>();

        // One provider warm-up per provider key, shared by every consumer group of that provider.
        services.TryAddSingleton<OutboxProviderInitialization>();

        services.TryAddSingleton<IDeliveryLifetimeInvoker, DeliveryLifetimeInvoker>();

        // The snapshot and the partitional support are per provider: the snapshot reads the
        // provider's own schedule and consumer groups, and the support enumerates the provider's own
        // tenants.
        services.TryAddKeyedSingleton<IDeliverySnapshot>(scopeKey, (sp, _) => new DeliverySnapshot(
            sp.GetRequiredKeyedService<IScheduleSettings>(scopeKey),
            sp.GetRequiredService<IOutboxConsumerManager>(),
            sp.GetRequiredService<IOutboxMessageMetadataProvider>(),
            scopeKey));

        services.TryAddKeyedSingleton<IOutboxPartitionalSupport>(scopeKey, (sp, _) =>
            new OutboxPartitionalSupport(
                sp.GetKeyedService<IDeliverySnapshot>(scopeKey),
                sp.GetService<ITenantProvider>(),
                scopeKey));

        if (string.Equals(scopeKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: the classic single-provider surface.
            services.TryAddSingleton<IOutboxPartitionalSupport>(
                sp => sp.GetRequiredKeyedService<IOutboxPartitionalSupport>(scopeKey));
        }
        services.TryAddSingleton<IOutboxConsumerManager>(sp => new OutboxConsumerManager(
            sp.GetServices<DeliveryGroupSettingsRegistration>(),
            sp,
            sp.GetService<ILogger<OutboxConsumerManager>>()));

        configure?.Invoke(new DeliveryBuilder(services, scopeKey));

        return services;
    }
}
