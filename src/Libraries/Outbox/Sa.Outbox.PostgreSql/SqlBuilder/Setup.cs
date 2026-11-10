using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSql.SqlBuilder;

/// <summary>Registers the outbox SQL builder.</summary>
public static class Setup
{
    internal static IServiceCollection AddOutboxSqlBuilder(
        this IServiceCollection services,
        PgOutboxConfiguration configuration)
    {
        string providerKey = configuration.ProviderKey;

        // ~20 SQL templates are composed from the table settings, so the builder is a singleton —
        // now one per provider key. The keyed registration uses an explicit factory: a keyed
        // registration of a concrete type would resolve its parameter unkeyed.
        services.TryAddKeyedSingleton<SqlOutboxBuilder>(providerKey, (sp, _) =>
            new SqlOutboxBuilder(sp.GetRequiredKeyedService<PgOutboxTableSettings>(providerKey)));

        return services;
    }
}
