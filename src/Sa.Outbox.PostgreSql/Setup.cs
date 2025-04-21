using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Interceptors;
using Sa.Outbox.PostgreSql.Partitional;
using Sa.Outbox.PostgreSql.Repository;
using Sa.Outbox.PostgreSql.TypeHashResolve;

namespace Sa.Outbox.PostgreSql;

public static class Setup
{
    public static IServiceCollection AddOutboxUsingPostgreSql(this IServiceCollection services, Action<IPgOutboxConfiguration>? configure = null)
    {
        services
            .AddSaInfrastructure();

        services.TryAddSingleton<SqlOutboxTemplate>();

        services
            .AddPgOutboxSettings(configure);

        services
            .AddOutboxMessageRepository()
            .AddOutboxPartitional()
            .AddIdGen()
            .AddOutboxCommands()
            .AddMsgTypeHashResolver()
            .AddOutboxJobInterceptors()
            ;

        services.AddOutbox();

        return services;
    }
}