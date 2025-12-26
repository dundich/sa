using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Repository.Plug;

namespace Sa.Outbox.PostgreSql.Repository;

internal static class Setup
{
    public static IServiceCollection AddOutboxMessageRepository(this IServiceCollection services)
    {
        PlugImplementation(services);

        services.TryAddSingleton<IOutboxPartRepository, OutboxPartRepository>();
        services.TryAddSingleton<IOutboxMsgTypeRepository, OutboxMsgTypeRepository>();
        services.TryAddSingleton<IOutboxTaskLoader, OutboxTaskLoader>();

        return services;
    }

    private static void PlugImplementation(IServiceCollection services)
    {
        services
            .RemoveAll<IOutboxBulkWriter>()
            .AddSingleton<IOutboxBulkWriter, OutboxBulkWriter>();

        services
            .RemoveAll<IOutboxDeliveryManager>()
            .AddSingleton<IOutboxDeliveryManager, OutboxDeliveryManager>();

        services
            .RemoveAll<IOutboxTenantDetector>()
            .AddSingleton<IOutboxTenantDetector, OutboxTenantDetector>();
    }
}
