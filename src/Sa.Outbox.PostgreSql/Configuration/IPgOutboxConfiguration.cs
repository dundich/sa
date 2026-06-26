using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.Serialization;
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
    /// Applies a configuration delegate to the root <see cref="PgOutboxSettings"/> object.
    /// Use this to customize table names, schema, migration, cleanup, and consume settings.
    /// </summary>
    /// <param name="configure">Delegate receiving the service provider and settings instance.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>? configure = null);

    /// <summary>
    /// Configures the PostgreSQL data source connection string and related options.
    /// </summary>
    /// <param name="configure">Delegate receiving <see cref="IPgDataSourceSettingsBuilder"/> to set connection string and pooling options.</param>
    /// <returns>The same <see cref="IPgOutboxConfiguration"/> for chaining.</returns>
    IPgOutboxConfiguration WithDataSource(Action<IPgDataSourceSettingsBuilder>? configure = null);
}
