using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;
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

        services.TryAddSingleton<IDeliveryTenant, DeliveryTenant>();

        services.TryAddSingleton<IDeliveryLifetimeInvoker, DeliveryLifetimeInvoker>();

        // DeliverySnapshot reads the outbox's own (keyed) schedule, not the application's default one.
        services.TryAddSingleton<IDeliverySnapshot>(sp => new DeliverySnapshot(
            sp.GetRequiredKeyedService<IScheduleSettings>(scopeKey),
            sp.GetRequiredService<IOutboxConsumerManager>(),
            sp.GetRequiredService<IOutboxMessageMetadataProvider>()));
        services.TryAddSingleton<IOutboxConsumerManager, OutboxConsumerManager>();

        configure?.Invoke(new DeliveryBuilder(services, scopeKey));

        return services;
    }
}
