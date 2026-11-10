using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Poison;

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

        services.TryAddKeyedSingleton<IOutboxProviderHealth>(providerKey, (sp, _) =>
            new PgOutboxHealth(PgDataSourceResolver.Resolve(sp, providerKey)));

        // One-time warm-up before the first delivery cycle (SELECT 1 on the provider's data source).
        services.TryAddKeyedSingleton<IOutboxProviderInitializer>(providerKey, (sp, _) =>
            new PgOutboxInitializer(PgDataSourceResolver.Resolve(sp, providerKey)));

        // Two providers on the same tables would deliver every message twice; the guard compares
        // the layouts at host start. One marker per provider key, one guard for all of them.
        // Unkeyed on purpose: an IEnumerable<T> injection does not see keyed registrations.
        // A repeated registration of the same key replaces its marker, so the guard never compares
        // a provider with itself.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor service = services[i];

            if (service.ServiceType == typeof(PgProviderLayout)
                && service.ImplementationInstance is PgProviderLayout existing
                && string.Equals(existing.ProviderKey, providerKey, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton(new PgProviderLayout(providerKey));
        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor
                .Singleton<Microsoft.Extensions.Hosting.IHostedService, PgMultiProviderGuard>());

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
                    sp.GetRequiredKeyedService<Sa.Outbox.Serialization.IOutboxMessageSerializer>(providerKey),
                    sp.GetKeyedService<IPgOutboxPoisonHandler>(providerKey)
                        ?? sp.GetRequiredService<IPgOutboxPoisonHandler>(),
                    sp.GetService<Microsoft.Extensions.Logging.ILogger<OutboxDeliveryManager>>()))
                .WithTenantDetector(sp => new OutboxTenantDetector(
                    sp.GetRequiredKeyedService<ISelectTenantCommand>(providerKey))));

        return services;
    }
}
