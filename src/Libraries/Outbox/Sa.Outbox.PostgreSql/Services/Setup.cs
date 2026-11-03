using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Services.Plug;
using Sa.Outbox.PostgreSql.SqlBuilder;
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

        return services;
    }
}
