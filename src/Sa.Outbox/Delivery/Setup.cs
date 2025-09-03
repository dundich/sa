using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Configuration;
using Sa.Outbox.Partitional;

namespace Sa.Outbox.Delivery;

internal static class Setup
{
    public static IServiceCollection AddOutboxDelivery(this IServiceCollection services, Action<IDeliveryBuilder> configure)
    {

        // looper - job processor
        services.TryAddSingleton<IDeliveryProcessor, DeliveryProcessor>();
        // iteration - extract from repository & batch & send to courier
        services.TryAddSingleton<IDeliveryRelay, DeliveryRelay>();
        // sender - sending to scope consumer
        services.TryAddSingleton<IDeliveryCourier, DeliveryCourier>();
        // support - messaging to each tenant
        services.TryAddSingleton<IPartitionalSupportCache, PartitionalSupportCache>();

        services.TryAddSingleton<IScopedConsumer, ScopedConsumer>();

        configure.Invoke(new DeliveryBuilder(services));

        return services;
    }
}
