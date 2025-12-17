using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Outbox.PostgreSql.Repository;

internal static class Setup
{
    public static IServiceCollection AddOutboxMessageRepository(this IServiceCollection services)
    {
        services.TryAddSingleton<IOutboxPartRepository, OutboxPartRepository>();
        services.TryAddSingleton<IOutboxRepository, OutboxRepository>();
        services.TryAddSingleton<IDeliveryRepository, DeliveryRepository>();
        services.TryAddSingleton<IMsgTypeRepository, MsgTypeRepository>();
        services.TryAddSingleton<IConsumeLoader, ConsumeLoader>();
        return services;
    }
}
