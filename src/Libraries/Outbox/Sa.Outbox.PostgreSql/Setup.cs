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

/// <summary>Registers the PostgreSQL-backed outbox provider.</summary>
public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL-backed Outbox.
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure the outbox through <see cref="IPgOutboxConfiguration"/>.</param>
    /// <param name="configureOutbox">
    /// An optional action to configure the provider-agnostic Outbox core (<see cref="IOutboxBuilder"/>):
    /// tenants, deliveries, metadata, serializer. Supplying it here is the one-call form and removes
    /// the need to repeat <paramref name="scopeKey"/> in a separate <c>AddSaOutbox</c> call.
    /// </param>
    /// <param name="scopeKey">
    /// The dependency-injection scope of the outbox — the key under which its internal
    /// <c>Sa.Partitional.PostgreSql</c> module and its named <c>Sa.Schedule</c> schedule (migration,
    /// cleanup and delivery jobs) are registered. Defaults to <see cref="OutboxScope.DefaultKey"/>.
    /// Pass it once here; the core is registered by this method, so no separate <c>AddSaOutbox</c>
    /// call is needed.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingPostgreSql(
        this IServiceCollection services,
        Action<IPgOutboxConfiguration>? configure,
        Action<IOutboxBuilder>? configureOutbox,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        PgOutboxConfiguration pg = services.CreatePgOutboxConfiguration(scopeKey, configure);

        // A disabled provider registers nothing: no settings, no keyed data source (whose
        // ValidateOnStart would fail on an empty connection string), no groups, no schedule.
        if (!pg.Enabled) return services;

        pg.Commit();

        services
            .AddOutboxSqlBuilder(pg)
            .AddOutboxRepositories(scopeKey)
            .AddOutboxPartitional(scopeKey)
            .AddOutboxIdGen()
            .AddOutboxCommands(scopeKey)
            .AddOutboxTypeResolver(scopeKey)
            .AddOutboxJobInterceptors(scopeKey)
            ;

        services.AddSaOutbox(configureOutbox, scopeKey);

        // Replace the base no-op delivery-group options provider with the PostgreSQL
        // implementation: it applies the polling defaults and binds a group's settings from the
        // IConfiguration section named by the configurationKey (or the convention) passed to
        // AddDelivery/AddDeliveryScoped. Both IConfiguration and PgPollingSettings are resolved as
        // optional services — absent, the corresponding layer is simply not applied.
        // Keyed per provider: polling defaults and the group section convention are provider-local.
        services
            .RemoveAll<IDeliveryGroupOptionsProvider>()
            .AddKeyedSingleton<IDeliveryGroupOptionsProvider>(
                scopeKey,
                (sp, _) => new PgOutboxDeliveryOptionsProvider(
                    sp.GetService<IConfiguration>(),
                    sp.GetKeyedService<PgPollingSettings>(scopeKey)));

        if (string.Equals(scopeKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: the classic single-provider API resolves the options provider
            // without a key.
            services.TryAddSingleton<IDeliveryGroupOptionsProvider>(
                sp => sp.GetRequiredKeyedService<IDeliveryGroupOptionsProvider>(scopeKey));
        }

        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL-backed Outbox and the provider-agnostic core in one call.
    /// </summary>
    /// <remarks>
    /// Convenience overload: identical to <c>AddSaOutboxUsingPostgreSql(configure, scopeKey)</c>
    /// followed by <c>AddSaOutbox(configureOutbox, scopeKey)</c>, with the scope key written once.
    /// </remarks>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure the PostgreSQL provider.</param>
    /// <param name="scopeKey">The shared outbox scope key.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingPostgreSql(
        this IServiceCollection services,
        Action<IPgOutboxConfiguration>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
        => services.AddSaOutboxUsingPostgreSql(configure, configureOutbox: null, scopeKey);
}
