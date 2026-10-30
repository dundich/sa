using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Interceptors;
using Sa.Outbox.PostgreSql.Partitional;
using Sa.Outbox.PostgreSql.Services;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSql;

public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL-backed Outbox.
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure the outbox through <see cref="IPgOutboxConfiguration"/>.</param>
    /// <param name="scopeKey">
    /// The dependency-injection scope of the outbox — the key under which its internal
    /// <c>Sa.Partitional.PostgreSql</c> module and its named <c>Sa.Schedule</c> schedule (migration,
    /// cleanup and delivery jobs) are registered. Defaults to <see cref="OutboxScope.DefaultKey"/>.
    /// Pass the same value to <c>AddSaOutbox</c> when you override it.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingPostgreSql(
        this IServiceCollection services,
        Action<IPgOutboxConfiguration>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        services
            .AddOutboxSqlBuilder(configure)
            .AddOutboxRepositories(scopeKey)
            .AddOutboxPartitional(scopeKey)
            .AddOutboxIdGen()
            .AddOutboxCommands()
            .AddOutboxTypeResolver()
            .AddOutboxJobInterceptors(scopeKey)
            ;

        services.AddSaOutbox(scopeKey: scopeKey);

        // Replace the base no-op delivery-group options provider with the PostgreSQL
        // implementation: it binds a group's settings from the IConfiguration section
        // named by the configurationKey passed to AddDelivery/AddDeliveryScoped. The
        // IConfiguration is resolved as an optional service — absent, the provider is a
        // no-op and the group is shaped purely by OutboxDefaults and the configure callback.
        services
            .RemoveAll<IDeliveryGroupOptionsProvider>()
            .AddSingleton<IDeliveryGroupOptionsProvider>(
                sp => new PgOutboxDeliveryOptionsProvider(sp.GetService<IConfiguration>()));

        return services;
    }
}
