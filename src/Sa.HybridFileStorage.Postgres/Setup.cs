using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.HybridFileStorage.Domain;
using Sa.Partitional.PostgreSql;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Provides extension methods for registering the PostgreSQL file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL file storage provider using the standard options pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
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
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    /// <remarks>
    /// The options pipeline runs in a fixed order: the section binding (<c>FromConfiguration</c>,
    /// raw values) and then the caller's <c>Options(...)</c> <c>Configure</c> calls → validation.
    /// Validation itself is the standard pipeline's: it sees the normalised result and fires at
    /// the first read of the options (the storage, the DDL or the schema resolver materialise
    /// them), or at host start with <c>ValidateOnStart()</c> — so an invalid table name or
    /// storage type fails as an <see cref="OptionsValidationException"/> naming the property
    /// instead of reaching the DDL and surfacing as <c>relation does not exist</c> on the first
    /// upload. In a test host without a started host the first read is what fires it.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<IPostgresFileStorageBuilder>? configure = null)
    {
        PostgresFileStorageRegistrar.Register(services, configure, instance: null);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider using the standard options pipeline.
    /// Returns an <see cref="IPartConfiguration"/> for chaining DataSource configuration.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">The configuration channel: section and pipeline actions in one delegate.</param>
    /// <returns>An <see cref="IPartConfiguration"/> for chaining DataSource configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    public static IPartConfiguration AddSaPostgreSqlFileStorageChained(
        this IServiceCollection services,
        Action<IPostgresFileStorageBuilder>? configure = null)
        => PostgresFileStorageRegistrar.Register(services, configure, instance: null);

    /// <summary>
    /// Registers the PostgreSQL file storage provider from an explicit options instance.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the PostgreSQL storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    /// <remarks>
    /// The instance is copied — the caller's object is never the storage's and never touched —
    /// and the copy feeds this registration's named pipeline instance as its only source, so
    /// validation runs here exactly as for the builder channel: at first read or host start,
    /// not at registration.
    /// </remarks>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        PostgresFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = PostgresFileStorageRegistrar.Register(services, configure: null, instance: options);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL file storage provider from a ready-made options instance,
    /// typically one the caller resolved from its own pipeline.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">The ready-made options; <see cref="IOptions{T}.Value"/> is read once, here.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a PostgreSQL storage was already registered in this collection.</exception>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        IOptions<PostgresFileStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = PostgresFileStorageRegistrar.Register(services, configure: null, instance: options.Value);
        return services;
    }
}
