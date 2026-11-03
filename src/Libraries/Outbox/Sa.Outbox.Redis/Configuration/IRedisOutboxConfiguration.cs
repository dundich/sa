using Microsoft.Extensions.Options;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Serialization;
using StackExchange.Redis;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.Redis.Configuration;

/// <summary>
/// Fluent configuration surface of the Redis-backed Outbox. Used inside
/// <see cref="Setup.AddSaOutboxUsingRedis"/> to point the provider at a Redis server and to shape
/// the key layout and consumption.
/// </summary>
/// <remarks>
/// The connection is resolved lazily, in this order: an explicit <see cref="IDatabase"/>, an
/// explicit <see cref="IConnectionMultiplexer"/>, a multiplexer or database already registered in
/// the container, then a connection string. Only a multiplexer this provider created itself is
/// disposed with the container.
/// </remarks>
public interface IRedisOutboxConfiguration
{
    /// <summary>
    /// Whether the provider is registered at all. Default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A disabled provider registers nothing — no settings, no connection (multiplexer, database or
    /// factory), no serializer or poison overrides — and the caller skips its consumer groups and
    /// schedule too. Useful for a configuration-driven on/off switch: the disabled provider cannot
    /// fail host validation with an absent server.
    /// </remarks>
    bool Enabled { get; }

    /// <summary>
    /// Turns the provider registration on or off. The call order inside the configuration callback
    /// does not matter: the value is evaluated when the registration is applied.
    /// </summary>
    /// <param name="enabled">Whether to register the provider.</param>
    /// <returns>The same <see cref="IRedisOutboxConfiguration"/> for chaining.</returns>
    IRedisOutboxConfiguration WithEnabled(bool enabled = true);

    /// <summary>
    /// Uses a ready <see cref="IConnectionMultiplexer"/>. Its lifetime stays the caller's: the Outbox
    /// never disposes a multiplexer it did not create.
    /// </summary>
    IRedisOutboxConfiguration WithConnectionMultiplexer(IConnectionMultiplexer multiplexer);

    /// <summary>
    /// Uses a factory that produces the <see cref="IConnectionMultiplexer"/> from the service
    /// provider. The factory owns the returned multiplexer; the Outbox does not dispose it.
    /// </summary>
    IRedisOutboxConfiguration WithConnectionMultiplexer(Func<IServiceProvider, IConnectionMultiplexer> multiplexerFactory);

    /// <summary>
    /// Uses a ready <see cref="IDatabase"/>. Its lifetime stays the caller's.
    /// </summary>
    IRedisOutboxConfiguration WithDatabase(IDatabase database);

    /// <summary>
    /// Uses a factory that produces the <see cref="IDatabase"/> from the service provider. The
    /// factory owns the returned database.
    /// </summary>
    IRedisOutboxConfiguration WithDatabase(Func<IServiceProvider, IDatabase> databaseFactory);

    /// <summary>
    /// Falls back to a connection string. <paramref name="connectionStringOrName"/> is resolved as
    /// follows: a value containing <c>=</c> or <c>:</c> is used verbatim as a StackExchange.Redis
    /// connection string; anything else is treated as a name and read from
    /// <c>IConfiguration.GetConnectionString(name)</c>.
    /// </summary>
    /// <remarks>
    /// This is the only mode in which the provider creates — and therefore disposes — the
    /// multiplexer itself.
    /// </remarks>
    /// <param name="connectionStringOrName">The connection string or its configuration name. Default is <c>Redis</c>.</param>
    IRedisOutboxConfiguration WithConnectionString(string connectionStringOrName = "Redis");

    /// <summary>Same as <see cref="WithConnectionString(string)"/>, resolved from the service provider.</summary>
    IRedisOutboxConfiguration WithConnectionString(Func<IServiceProvider, string?> connectionStringOrNameFactory);

    /// <summary>
    /// Adjusts the <see cref="ConfigurationOptions"/> used when the provider opens its own
    /// connection. Ignored when a ready multiplexer or database is supplied.
    /// </summary>
    IRedisOutboxConfiguration WithConnectionOptions(Action<ConfigurationOptions> configureOptions);

    /// <summary>Selects the logical Redis database index. Default is <c>0</c>.</summary>
    IRedisOutboxConfiguration WithDatabaseIndex(int databaseIndex);

    /// <summary>
    /// Applies a configuration delegate to the root <see cref="RedisOutboxSettings"/> object: key
    /// prefix, group start mode, pending claim, delivery limit and retention.
    /// </summary>
    /// <remarks>
    /// The delegate is a code default: it runs before <see cref="WithConfiguration"/> and before the
    /// <see cref="Options"/> actions, so configuration and those actions override it.
    /// </remarks>
    IRedisOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, RedisOutboxSettings>? configure = null);

    /// <summary>
    /// Binds <see cref="RedisOutboxSettings"/> from a configuration section, for example
    /// <c>WithConfiguration("Sa:Outbox:Redis")</c>.
    /// </summary>
    /// <remarks>
    /// The binder only overwrites keys the section contains, so code values of
    /// <see cref="WithOutboxSettings"/> survive for absent keys while configuration wins for present
    /// ones. <see cref="Options"/> actions run after the section and have the last word.
    /// </remarks>
    /// <param name="configSectionPath">The path of the settings section.</param>
    IRedisOutboxConfiguration WithConfiguration(string configSectionPath);

    /// <summary>
    /// Adds actions to the standard <see cref="OptionsBuilder{TOptions}"/> of
    /// <see cref="RedisOutboxSettings"/>: the escape hatch over both the code defaults and the
    /// configuration section.
    /// </summary>
    /// <param name="configureOptions">The actions to add to the options pipeline.</param>
    IRedisOutboxConfiguration Options(Action<OptionsBuilder<RedisOutboxSettings>> configureOptions);

    /// <summary>
    /// Registers a serializer factory for this provider key. Providers without their own serializer
    /// keep following the container-level registration (the core JSON serializer is the fallback).
    /// </summary>
    IRedisOutboxConfiguration WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory);

    /// <summary>
    /// Replaces the poison handler: the sink for messages whose pending delivery counter exceeded
    /// <see cref="RedisOutboxConsumeSettings.MaxDeliveryAttempts"/> and for permanent consumer
    /// outcomes. Without this call the provider logs a warning and acknowledges the entry.
    /// </summary>
    /// <param name="poisonHandlerFactory">A factory that produces the handler from DI.</param>
    IRedisOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IRedisOutboxPoisonHandler> poisonHandlerFactory);

    /// <summary>Registers a concrete poison handler instance.</summary>
    IRedisOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IRedisOutboxPoisonHandler;

    /// <summary>Registers a poison handler type resolved from DI.</summary>
    IRedisOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IRedisOutboxPoisonHandler;

    /// <summary>Registers a concrete serializer instance for this provider's key.</summary>
    IRedisOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer;

    /// <summary>Registers a serializer type for this provider key, resolved from DI.</summary>
    IRedisOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer;
}
