using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal static class Setup
{
    public static IServiceCollection AddOutboxPlugins(this IServiceCollection services)
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

        return services;
    }
}
