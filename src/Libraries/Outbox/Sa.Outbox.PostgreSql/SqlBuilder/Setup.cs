using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSql.SqlBuilder;

public static class Setup
{
    internal static IServiceCollection AddOutboxSqlBuilder(
        this IServiceCollection services,
        string providerKey,
        Action<IPgOutboxConfiguration>? configure = null)
    {
        services.AddPgOutboxSettings(providerKey, configure);

        // ~20 SQL templates are composed from the table settings, so the builder is a singleton —
        // now one per provider key. The keyed registration uses an explicit factory: a keyed
        // registration of a concrete type would resolve its parameter unkeyed.
        services.TryAddKeyedSingleton<SqlOutboxBuilder>(providerKey, (sp, _) =>
            new SqlOutboxBuilder(sp.GetRequiredKeyedService<PgOutboxTableSettings>(providerKey)));

        return services;
    }
}
