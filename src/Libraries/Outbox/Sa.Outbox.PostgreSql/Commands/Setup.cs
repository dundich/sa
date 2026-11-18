using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.PostgreSql.Commands;

internal static class Setup
{
    public static IServiceCollection AddOutboxCommands(this IServiceCollection services, string providerKey)
    {
        // The reader caches column ordinals from the provider's table settings, so it is keyed
        // like the rest of the storage chain.
        services.TryAddKeyedSingleton<NpgsqlOutboxReader>(providerKey, (sp, _) =>
            new NpgsqlOutboxReader(sp.GetRequiredKeyedService<PgOutboxTableSettings>(providerKey)));

        services.TryAddKeyedSingleton<IBulkInsertMsgCommand>(providerKey, (sp, _) => new BulkInsertMsgCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey),
            sp.GetRequiredKeyedService<IOutboxMessageSerializer>(providerKey),
            sp.GetRequiredService<IOutboxIdGenerator>(),
            sp.GetRequiredKeyedService<IOutboxTypeResolver>(providerKey)));

        services.TryAddKeyedSingleton<IStartDeliveryCommand>(providerKey, (sp, _) => new StartDeliveryCommand(
            sp.GetRequiredService<IOutboxContextFactory>(),
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey),
            sp.GetRequiredKeyedService<IOutboxMessageSerializer>(providerKey),
            sp.GetRequiredKeyedService<IOutboxTypeResolver>(providerKey),
            sp.GetRequiredKeyedService<NpgsqlOutboxReader>(providerKey)));

        services.TryAddKeyedSingleton<IErrorDeliveryCommand>(providerKey, (sp, _) => new ErrorDeliveryCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey)));

        services.TryAddKeyedSingleton<IFinishDeliveryCommand>(providerKey, (sp, _) => new FinishDeliveryCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey),
            sp.GetService<ILogger<FinishDeliveryCommand>>()));

        services.TryAddKeyedSingleton<IExtendDeliveryCommand>(providerKey, (sp, _) => new ExtendDeliveryCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<IOutboxTypeResolver>(providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey)));

        services.TryAddKeyedSingleton<IInsertMsgTypeCommand>(providerKey, (sp, _) => new InsertMsgTypeCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey)));

        services.TryAddKeyedSingleton<ISelectMsgTypeCommand>(providerKey, (sp, _) => new SelectMsgTypeCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey),
            sp.GetRequiredKeyedService<NpgsqlOutboxReader>(providerKey)));

        services.TryAddKeyedSingleton<ISelectTenantCommand>(providerKey, (sp, _) => new SelectTenantCommand(
            PgDataSourceResolver.Resolve(sp, providerKey),
            sp.GetRequiredKeyedService<SqlOutboxBuilder>(providerKey)));

        return services;
    }
}
