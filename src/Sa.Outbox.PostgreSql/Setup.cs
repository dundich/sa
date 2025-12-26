using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Interceptors;
using Sa.Outbox.PostgreSql.Partitional;
using Sa.Outbox.PostgreSql.Repository;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSql;

public static class Setup
{
    public static IServiceCollection AddOutboxUsingPostgreSql(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<SqlOutboxTemplate>();

        services
            .AddPgOutboxSettings(configure);

        services
            .AddOutboxRepositories()
            .AddOutboxPartitional()
            .AddOutboxIdGen()
            .AddOutboxCommands()
            .AddOutboxTypeResolver()
            .AddOutboxJobInterceptors()
            ;

        services.AddOutbox();

        return services;
    }
}