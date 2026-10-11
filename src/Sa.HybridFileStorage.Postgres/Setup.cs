using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Provides extension methods for registering keyed PostgreSQL file storage providers with the .NET Generic Host.
/// </summary>
/// <remarks>
/// Registration is <b>keyed</b>: every overload takes a registration <c>key</c> that names the storage
/// and its keyed services. Each key gets its own options instance (its own settings and retention) and
/// its own partitional module — so a whole cluster of independent PostgreSQL storages, each owning a
/// table, can live in one container. Every registration is also aliased as an unkeyed
/// <see cref="IFileStorage"/>, which is what the hybrid container turns into the failover chain: the
/// storages are tried in registration order and the first success ends the operation.
/// </remarks>
public static class Setup
{
    /// <summary>
    /// Registers a keyed PostgreSQL file storage provider using the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="key">
    /// The registration key. It names the storage's options instance and its keyed <see cref="IFileStorage"/>
    /// (<c>sp.GetRequiredKeyedService&lt;IFileStorage&gt;(key)</c>). Must be non-empty and unique per
    /// collection: a second registration with the same key fails fast.
    /// </param>
    /// <param name="configure">
    /// The configuration channel: the section via <see cref="IPostgresFileStorageBuilder.FromConfiguration"/>
    /// and the standard pipeline (<c>Configure</c> / <c>PostConfigure</c> / <c>Validate</c>) via
    /// <see cref="IPostgresFileStorageBuilder.Options"/>, in the same delegate. Invoked once, immediately;
    /// its <c>Options(...)</c> actions are replayed after this method's own registrations, so their
    /// <c>Configure</c> runs last and their <c>Validate</c> adds to — rather than replaces — the
    /// built-in checks.
    /// </param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a storage with the same <paramref name="key"/> is already registered.</exception>
    /// <remarks>
    /// <para>
    /// Each registration owns its own table: <see cref="PostgresFileStorageOptions.TableName"/> is
    /// required and may not be shared with another registration (two storages over one physical table
    /// would compete over migration and retention).
    /// </para>
    /// <para>
    /// The options pipeline runs in a fixed order: the section binding (<c>FromConfiguration</c>,
    /// raw values) and then the caller's <c>Options(...)</c> <c>Configure</c> calls → validation.
    /// Validation itself is the standard pipeline's: it sees the normalised result and fires at
    /// the first read of the options (the storage, the DDL or the schema resolver materialise
    /// them), or at host start with <c>ValidateOnStart()</c> — so an invalid table name, a missing
    /// <see cref="PostgresFileStorageOptions.TableName"/> or a table already claimed by another
    /// registration fails as an <see cref="OptionsValidationException"/> naming the property
    /// instead of reaching the DDL and surfacing as <c>relation does not exist</c> on the first
    /// upload. In a test host without a started host the first read is what fires it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        string key,
        Action<IPostgresFileStorageBuilder>? configure = null)
    {
        PostgresFileStorageRegistrar.Register(services, key, configure, instance: null);
        return services;
    }

    /// <summary>
    /// Registers a keyed PostgreSQL file storage provider using the standard options pipeline.
    /// Returns an <see cref="IPartConfiguration"/> for chaining DataSource configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="key">The registration key; must be non-empty and unique per collection.</param>
    /// <param name="configure">The configuration channel: section and pipeline actions in one delegate.</param>
    /// <returns>An <see cref="IPartConfiguration"/> for chaining DataSource configuration (scoped to this registration).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a storage with the same <paramref name="key"/> is already registered.</exception>
    /// <remarks>
    /// The returned configuration is the storage's own keyed partitional module, so
    /// <c>AddDataSource(...)</c> pins a data source named under this registration without affecting the
    /// default data source or any other storage.
    /// </remarks>
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        string key,
        Action<IPostgresFileStorageBuilder>? configure = null)
        => PostgresFileStorageRegistrar.Register(services, key, configure, instance: null);

    /// <summary>
    /// Registers a keyed PostgreSQL file storage provider from an explicit options instance.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="key">The registration key; must be non-empty and unique per collection.</param>
    /// <param name="options">Configuration options for the PostgreSQL storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a storage with the same <paramref name="key"/> is already registered.</exception>
    /// <remarks>
    /// The instance is copied — the caller's object is never the storage's and never touched —
    /// and the copy feeds this registration's named pipeline instance as its only source, so
    /// validation runs here exactly as for the builder channel: at first read or host start,
    /// not at registration.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        string key,
        PostgresFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = PostgresFileStorageRegistrar.Register(services, key, configure: null, instance: options);
        return services;
    }

    /// <summary>
    /// Registers a keyed PostgreSQL file storage provider from a ready-made options instance,
    /// typically one the caller resolved from its own pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="key">The registration key; must be non-empty and unique per collection.</param>
    /// <param name="options">The ready-made options; <see cref="IOptions{T}.Value"/> is read once, here.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a storage with the same <paramref name="key"/> is already registered.</exception>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        string key,
        IOptions<PostgresFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = PostgresFileStorageRegistrar.Register(services, key, configure: null, instance: options.Value);
        return services;
    }
}
