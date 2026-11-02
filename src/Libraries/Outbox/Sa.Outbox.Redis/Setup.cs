using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Services;

namespace Sa.Outbox.Redis;

public static class Setup
{
    /// <summary>
    /// Registers the Redis Streams-backed Outbox.
    /// </summary>
    /// <remarks>
    /// Messages are appended to a Redis stream per (tenant, part, payload type) and consumed through
    /// native consumer groups (<c>XREADGROUP</c>/<c>XAUTOCLAIM</c>/<c>XACK</c>). The provider keeps no
    /// state outside the stream: unacknowledged entries stay in the group's pending entries list and
    /// are reclaimed once they have been idle for <c>PendingClaimIdle</c>. Every command is a plain
    /// Redis call — the provider evaluates no Lua scripts — and every command touches a single key.
    /// </remarks>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">
    /// An optional action to configure the provider through <see cref="IRedisOutboxConfiguration"/>:
    /// the Redis connection, the key layout and group consumption.
    /// </param>
    /// <param name="configureOutbox">
    /// An optional action to configure the provider-agnostic Outbox core (<see cref="IOutboxBuilder"/>):
    /// tenants, deliveries, metadata, serializer. Supplying it here is the one-call form and removes
    /// the need to repeat <paramref name="scopeKey"/> in a separate <c>AddSaOutbox</c> call.
    /// </param>
    /// <param name="scopeKey">
    /// The provider key and dependency-injection scope of the outbox — the key under which its
    /// internal <c>Sa.Schedule</c> schedule is registered and under which the provider contracts are
    /// keyed. Defaults to <see cref="OutboxScope.DefaultKey"/>; use one key per provider when the
    /// container holds several.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingRedis(
        this IServiceCollection services,
        Action<IRedisOutboxConfiguration>? configure,
        Action<IOutboxBuilder>? configureOutbox,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        services
            .AddRedisOutboxSettings(configure, scopeKey)
            .AddOutboxServices(scopeKey);

        // The core publisher takes a TimeProvider; Sa.Schedule registers System only as a side
        // effect of the partitional/migration module, which the Redis provider does not have.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSaOutbox(configureOutbox, scopeKey);

        return services;
    }

    /// <summary>
    /// Registers the Redis Streams-backed Outbox with the default key (the classic single-provider
    /// form).
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure the provider.</param>
    /// <param name="scopeKey">The outbox scope key; pass the same value to <c>AddSaOutbox</c> when the core is registered separately.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty, whitespace, or the reserved default name.</exception>
    public static IServiceCollection AddSaOutboxUsingRedis(
        this IServiceCollection services,
        Action<IRedisOutboxConfiguration>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
        => services.AddSaOutboxUsingRedis(configure, configureOutbox: null, scopeKey);
}
