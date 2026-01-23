using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Interceptors;
using Sa.Outbox.PostgreSql.Partitional;
using Sa.Outbox.PostgreSql.Services;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSql;

public static class Setup
{
    public static IServiceCollection AddOutboxUsingPostgreSql(
        this IServiceCollection services,
        Action<IPgOutboxConfiguration>? configure = null)
    {
        services
            .AddOutboxSqlBuilder(configure)
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
