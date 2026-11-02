using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>
/// Fluent configuration surface for the PostgreSQL-backed Outbox subsystem.
/// Used inside <see cref="Setup.AddSaOutboxUsingPostgreSql"/> to wire up data source,
/// message serialization, and table/migration/cleanup settings.
/// </summary>
public interface IPgOutboxConfiguration
{
    /// <summary>
    /// Replaces the default JSON serializer with a factory that produces
    /// <see cref="IOutboxMessageSerializer"/> instances from the DI container.
    /// </summary>
    /// <param name="messageSerializerFactory">Factory invoked at runtime to create serializer instances.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithMessageSerializer(
        Func<IServiceProvider, IOutboxMessageSerializer> messageSerializerFactory);

    /// <summary>
    /// Registers a concrete <typeparamref name="TService"/> instance as the global
    /// <see cref="IOutboxMessageSerializer"/>. The instance is used directly without DI resolution.
    /// </summary>
    /// <typeparam name="TService">Concrete serializer type implementing <see cref="IOutboxMessageSerializer"/>.</typeparam>
    /// <param name="instance">Pre-created serializer instance.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithMessageSerializer<TService>(TService instance)
        where TService : class, IOutboxMessageSerializer;

    /// <summary>
    /// Registers a transient serializer of type <typeparamref name="TService"/> in DI.
    /// The type must implement <see cref="IOutboxMessageSerializer"/> and have a parameterless constructor.
    /// </summary>
    /// <typeparam name="TService">Serializer type to register in DI.</typeparam>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithMessageSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IOutboxMessageSerializer;

    /// <summary>
    /// Registers an interceptor for permanent failures (a consumer <c>Error</c>/<c>Error5xx</c>, or
    /// <c>MaximumAttemptsError</c>). Its return value chooses the strategy:
    /// <c>Default</c> keeps the built-in <c>__error$</c> persistence, <c>Handled</c> skips it and
    /// lets the handler own the error record.
    /// </summary>
    /// <param name="poisonHandlerFactory">A factory that produces the handler from DI.</param>
    IPgOutboxConfiguration WithPoisonHandler(Func<IServiceProvider, IPgOutboxPoisonHandler> poisonHandlerFactory);

    /// <summary>Registers a concrete poison handler instance.</summary>
    IPgOutboxConfiguration WithPoisonHandler<TService>(TService instance)
        where TService : class, IPgOutboxPoisonHandler;

    /// <summary>Registers a poison handler type resolved from DI.</summary>
    IPgOutboxConfiguration WithPoisonHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TService>()
        where TService : class, IPgOutboxPoisonHandler;

    /// <summary>
    /// Applies a configuration delegate to the root <see cref="PgOutboxSettings"/> object.
    /// Use this to customize table names, schema, migration, cleanup, and consume settings.
    /// </summary>
    /// <param name="configure">Delegate receiving the service provider and settings instance.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null);

    /// <summary>
    /// Registers polling defaults for every consumer group of this PostgreSQL Outbox that does not
    /// override them (a group's <c>IConfiguration</c> section and its <c>AddDelivery</c> callback
    /// both win over these values).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional: without this call no <see cref="PgPollingSettings"/> service is registered at all,
    /// so the provider keeps the previous behaviour (the core <c>OutboxDefaults</c> and the group's
    /// own configuration). This is the polling-only way to bound delivery latency — no notifications
    /// or other push mechanisms are involved.
    /// </para>
    /// <para>
    /// Prefer the <see cref="PgOutboxPollingExtensions.WithPollingBudget"/> /
    /// <see cref="PgOutboxPollingExtensions.WithPollingProfile"/> /
    /// <see cref="PgOutboxPollingExtensions.WithPollingFromConfiguration"/> helpers, which derive a
    /// consistent profile from a latency budget, from a named preset, or from configuration.
    /// </para>
    /// </remarks>
    /// <param name="configure">
    /// A delegate that mutates the settings; it runs once, when the settings are materialised from
    /// the container, after which they are validated.
    /// </param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithPolling(Action<IServiceProvider, PgPollingSettings>? configure = null);

    /// <summary>
    /// Configures the PostgreSQL data source through the standard options pipeline
    /// (<see cref="PgDataSourceOptions"/>).
    /// </summary>
    /// <param name="configure">
    /// Optional callback receiving the <see cref="IDataSourceBuilder"/> for this data source:
    /// the section via <c>FromConfiguration</c> and the options pipeline via <c>Options</c>
    /// (connection string; pooling, timeout and search path live inside the string itself).
    /// </param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithDataSource(Action<IDataSourceBuilder>? configure = null);
}
