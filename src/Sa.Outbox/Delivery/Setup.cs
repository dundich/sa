using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Configuration;
using Sa.Outbox.Partitional;

namespace Sa.Outbox.Delivery;

internal static class Setup
{
    public static IServiceCollection AddOutboxDelivery(this IServiceCollection services, Action<IDeliveryBuilder> configure)
    {

        services.TryAddSingleton<IDeliveryBatcher, DeliveryBatcher>();

        // looper - job processor
        services.TryAddSingleton<IDeliveryProcessor, DeliveryProcessor>();
        // sender - sending to scope consumer
        services.TryAddSingleton<IDeliveryCourier, DeliveryCourier>();
        // support - messaging to each tenant
        services.TryAddSingleton<IPartitionalSupportCache, PartitionalSupportCache>();

        services.TryAddSingleton<IDeliveryTenant, DeliveryTenant>();

        services.TryAddSingleton<IDeliveryScoped, DeliveryScoped>();

        configure.Invoke(new DeliveryBuilder(services));

        services.TryAddSingleton<IDelivarySnapshot, DelivarySnapshot>();

        return services;
    }
}
