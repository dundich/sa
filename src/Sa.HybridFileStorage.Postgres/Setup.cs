using Microsoft.Extensions.DependencyInjection;

namespace Sa.HybridFileStorage.Postgres;

/// <summary>
/// Provides extension methods for registering the PostgreSQL file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the PostgreSQL file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure the PostgreSQL storage via <see cref="IPostgresFileStorageConfiguration"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    public static IServiceCollection AddSaPostgreSqlFileStorage(
        this IServiceCollection services,
        Action<IPostgresFileStorageConfiguration>? configure = null)
    {
        PostgresFileStorageConfiguration configurator = new(services);
        configure?.Invoke(configurator);
        return services;
    }
}
