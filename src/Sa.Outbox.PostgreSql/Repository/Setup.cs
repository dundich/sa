using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PlugRepositories;

namespace Sa.Outbox.PostgreSql.Repository;

internal static class Setup
{
    public static IServiceCollection AddOutboxMessageRepository(this IServiceCollection services)
    {
        PlugImplementation(services);

        services.TryAddSingleton<IOutboxPartRepository, OutboxPartRepository>();
        services.TryAddSingleton<IMsgTypeRepository, MsgTypeRepository>();
        services.TryAddSingleton<IOutboxTaskLoader, OutboxTaskLoader>();
        return services;
    }

    private static void PlugImplementation(IServiceCollection services)
    {
        services
            .RemoveAll<IOutboxRepository>()
            .AddSingleton<IOutboxRepository, OutboxRepository>();

        services
            .RemoveAll<IDeliveryRepository>()
            .AddSingleton<IDeliveryRepository, DeliveryRepository>();
    }
}
