using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSql.SqlBuilder;

public static class Setup
{
    internal static IServiceCollection AddOutboxSqlBuilder(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        services.TryAddSingleton<SqlOutboxBuilder>();

        services.AddPgOutboxSettings(configure);

        return services;
    }
}