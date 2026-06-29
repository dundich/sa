using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery.Job;
using Sa.Outbox.Metadata;
using Sa.Outbox.Partitional;

namespace Sa.Outbox.Delivery;

internal static class Setup
{
    public static IServiceCollection AddOutboxDelivery(
        this IServiceCollection services, Action<IDeliveryBuilder>? configure = null)
    {
        services.AddMessagesMetadata();

        services.TryAddSingleton<FilterFactory>();

        services.TryAddSingleton<IOutboxContextFactory, OutboxContextFactory>();

        services.TryAddSingleton<IDeliveryBatcher, DeliveryBatcher>();

        // looper - job processor
        services.TryAddSingleton<IDeliveryProcessor, DeliveryProcessor>();
        // sender - sending to scope consumer
        services.TryAddSingleton<IDeliveryCourier, DeliveryCourier>();

        // support - messaging to each tenant
        services.AddOutboxPartitional();

        services.TryAddSingleton<IDeliveryTenant, DeliveryTenant>();

        services.TryAddSingleton<IDeliveryLifetimeInvoker, DeliveryLifetimeInvoker>();

        // DeliverySnapshot теперь собирает настройки из AddDeliveryJob через статический регистр
        services.TryAddSingleton<IDeliverySnapshot, DeliverySnapshot>();
        services.TryAddSingleton<IOutboxSettingsManager, OutboxSettingsManager>();

        configure?.Invoke(new DeliveryBuilder(services));

        // Bootstrap: register all consumer group initial settings into IOutboxSettingsManager.
        services.AddHostedService<OutboxSettingsBootstrap>();

        return services;
    }
}
