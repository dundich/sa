using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal static class Setup
{
    public static IServiceCollection AddOutboxPlugins(this IServiceCollection services)
    {
        // PostgreSQL already persists permanent failures in __error$; the handler is an additional
        // sink, no-op by default, replaced through WithPoisonHandler.
        services.TryAddSingleton<IPgOutboxPoisonHandler, NoOpPgOutboxPoisonHandler>();

        services
            .RemoveAll<IOutboxBulkWriter>()
            .AddSingleton<IOutboxBulkWriter, OutboxBulkWriter>();

        services
            .RemoveAll<IOutboxDeliveryManager>()
            .AddSingleton<IOutboxDeliveryManager, OutboxDeliveryManager>();

        services
            .RemoveAll<IOutboxTenantDetector>()
            .AddSingleton<IOutboxTenantDetector, OutboxTenantDetector>();

        services.TryAddSingleton<IPgOutboxDatabaseGuard, PgOutboxDatabaseGuard>();

        return services;
    }
}
