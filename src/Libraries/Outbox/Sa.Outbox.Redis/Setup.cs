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
    /// An optional action to configure the outbox through <see cref="IRedisOutboxConfiguration"/>:
    /// the Redis connection, the key layout and group consumption.
    /// </param>
    /// <param name="scopeKey">
    /// The dependency-injection scope of the outbox — the key under which its internal
    /// <c>Sa.Schedule</c> schedule (delivery jobs) is registered. Defaults to
    /// <see cref="OutboxScope.DefaultKey"/>. Pass the same value to <c>AddSaOutbox</c> when you
    /// override it.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingRedis(
        this IServiceCollection services,
        Action<IRedisOutboxConfiguration>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        services
            .AddRedisOutboxSettings(configure)
            .AddOutboxServices();

        // The core publisher takes a TimeProvider; Sa.Schedule registers System only as a side
        // effect of the partitional/migration module, which the Redis provider does not have.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSaOutbox(scopeKey: scopeKey);

        return services;
    }
}
