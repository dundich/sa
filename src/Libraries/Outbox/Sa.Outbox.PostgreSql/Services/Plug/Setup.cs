using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal static class Setup
{
    public static IServiceCollection AddOutboxPlugins(this IServiceCollection services, string providerKey)
    {
        // PostgreSQL already persists permanent failures in __error$, so this handler is an
        // additional sink. The NoOp default is unkeyed (the classic override registers it without a
        // key); WithPoisonHandler registers a keyed one per provider, and the delivery manager
        // prefers the keyed registration.
        services.TryAddSingleton<IPgOutboxPoisonHandler, NoOpPgOutboxPoisonHandler>();

        services.TryAddKeyedSingleton<IPgOutboxDatabaseGuard>(providerKey, (sp, _) =>
            new PgOutboxDatabaseGuard(PgDataSourceResolver.Resolve(sp, providerKey)));

        // The single provider-registration point: identity, capabilities, the three storage
        // contracts and the startup validation. Publish joins the caller's PgTransaction, tenant
        // detection reads the tenant list, lock renewal is honoured, and Postpone/Retry delays are
        // applied through lock_expires_on.
        services.AddOutboxProvider(
            providerKey,
            new OutboxProviderDescriptor(
                "PostgreSql",
                OutboxProviderCapabilities.PublishContext
                | OutboxProviderCapabilities.TenantDetection
                | OutboxProviderCapabilities.LockExtension
                | OutboxProviderCapabilities.Postponement,
                Version: typeof(Setup).Assembly.GetName().Version?.ToString()),
            provider => provider
                .WithBulkWriter(sp => new OutboxBulkWriter(
                    sp.GetRequiredKeyedService<IBulkInsertMsgCommand>(providerKey),
                    sp.GetRequiredKeyedService<IOutboxPartRepository>(providerKey),
                    sp.GetRequiredKeyedService<IPgOutboxDatabaseGuard>(providerKey)))
                .WithDeliveryManager(sp => new OutboxDeliveryManager(
                    sp.GetRequiredKeyedService<IStartDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<IErrorDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<IFinishDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<IExtendDeliveryCommand>(providerKey),
                    sp.GetRequiredKeyedService<IOutboxPartRepository>(providerKey),
                    sp.GetRequiredKeyedService<IOutboxTaskLoader>(providerKey),
                    sp.GetRequiredService<Sa.Outbox.Serialization.IOutboxMessageSerializer>(),
                    sp.GetKeyedService<IPgOutboxPoisonHandler>(providerKey)
                        ?? sp.GetRequiredService<IPgOutboxPoisonHandler>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<OutboxDeliveryManager>>()))
                .WithTenantDetector(sp => new OutboxTenantDetector(
                    sp.GetRequiredKeyedService<ISelectTenantCommand>(providerKey))));

        return services;
    }
}
