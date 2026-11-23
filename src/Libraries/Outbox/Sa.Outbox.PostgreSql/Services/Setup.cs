using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Services.Plug;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.Tracking;
using Sa.Outbox.PostgreSql.TypeResolve;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Services;

internal static class Setup
{
    public static IServiceCollection AddOutboxRepositories(this IServiceCollection services, string scopeKey)
    {
        services.AddOutboxPlugins(scopeKey);

        // The partitional module is keyed by the outbox scope; its manager cannot be
        // constructor-injected into a keyed singleton, so resolve the keyed one explicitly.
        services.TryAddKeyedSingleton<IOutboxPartRepository>(scopeKey, (sp, _) =>
            new OutboxPartRepository(
                sp.GetRequiredKeyedService<IPartitionManager>(scopeKey),
                sp.GetRequiredKeyedService<PgOutboxTableSettings>(scopeKey)));

        services.TryAddKeyedSingleton<IOutboxMsgTypeRepository>(scopeKey, (sp, _) =>
            new OutboxMsgTypeRepository(
                sp.GetRequiredKeyedService<IInsertMsgTypeCommand>(scopeKey),
                sp.GetRequiredKeyedService<ISelectMsgTypeCommand>(scopeKey)));

        services.TryAddKeyedSingleton<IOutboxTaskLoader>(scopeKey, (sp, _) =>
            new OutboxTaskLoader(
                PgDataSourceResolver.Resolve(sp, scopeKey),
                sp.GetRequiredKeyedService<SqlOutboxBuilder>(scopeKey),
                sp.GetRequiredKeyedService<PgOutboxConsumeSettings>(scopeKey),
                sp.GetRequiredKeyedService<IOutboxTypeResolver>(scopeKey),
                sp.GetService<ILogger<OutboxTaskLoader>>()));

        // Read-only diagnostic surface. TimeProvider is optional: the core publisher requires one, but
        // a publish-only deployment registers no delivery module, and the tracker can fall back to the
        // system clock (its only use of the clock is the default window and "has the lease expired").
        services.TryAddKeyedSingleton<IPgOutboxMessageTracker>(scopeKey, (sp, _) =>
            new PgOutboxMessageTracker(
                PgDataSourceResolver.Resolve(sp, scopeKey),
                sp.GetRequiredKeyedService<SqlOutboxBuilder>(scopeKey),
                sp.GetRequiredKeyedService<PgOutboxTableSettings>(scopeKey),
                sp.GetService<TimeProvider>()));

        // The write half of the diagnostic surface: requeueing a settled delivery. Deliberately not
        // folded into the tracker — read access to the outbox should not imply the right to replay it.
        services.TryAddKeyedSingleton<IPgOutboxMessageMaintenance>(scopeKey, (sp, _) =>
            new PgOutboxMessageMaintenance(
                PgDataSourceResolver.Resolve(sp, scopeKey),
                sp.GetRequiredKeyedService<SqlOutboxBuilder>(scopeKey),
                sp.GetRequiredKeyedService<PgOutboxTableSettings>(scopeKey),
                sp.GetService<TimeProvider>()));

        if (string.Equals(scopeKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: the classic single-provider API resolves the tracker without a key.
            services.TryAddSingleton<IPgOutboxMessageTracker>(
                sp => sp.GetRequiredKeyedService<IPgOutboxMessageTracker>(scopeKey));
            services.TryAddSingleton<IPgOutboxMessageMaintenance>(
                sp => sp.GetRequiredKeyedService<IPgOutboxMessageMaintenance>(scopeKey));
        }

        return services;
    }
}
