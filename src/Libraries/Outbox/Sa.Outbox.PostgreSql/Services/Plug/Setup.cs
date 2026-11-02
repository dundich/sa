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

        // The single provider-registration point: identity, capabilities, the three storage
        // contracts and the startup validation. Publish joins the caller's PgTransaction, tenant
        // detection reads the tenant list, lock renewal is honoured, and Postpone/Retry delays are
        // applied through lock_expires_on.
        services.AddOutboxProvider(
            new OutboxProviderDescriptor(
                "PostgreSql",
                OutboxProviderCapabilities.PublishContext
                | OutboxProviderCapabilities.TenantDetection
                | OutboxProviderCapabilities.LockExtension
                | OutboxProviderCapabilities.Postponement),
            provider => provider
                .WithBulkWriter<OutboxBulkWriter>()
                .WithDeliveryManager<OutboxDeliveryManager>()
                .WithTenantDetector<OutboxTenantDetector>());

        services.TryAddSingleton<IPgOutboxDatabaseGuard, PgOutboxDatabaseGuard>();

        return services;
    }
}
