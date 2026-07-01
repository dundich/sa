using Microsoft.Extensions.DependencyInjection;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Provides extension methods for registering hybrid file storage services with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the hybrid file storage infrastructure with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure the storage container and interceptors.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    public static IServiceCollection AddSaHybridFileStorage(
        this IServiceCollection services,
        Action<IHybridFileStorageConfiguration>? configure = null)
    {
        HybridStorageBuilder builder = new(services);
        configure?.Invoke(builder);
        builder.Build();
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Optional configuration options for the in-memory storage. If <c>null</c>, a default instance is used.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        InMemoryFileStorageOptions? options = null)
    {
        options ??= new(string.Empty);

        services.AddSingleton<IFileStorage, InMemoryFileStorage>(
            sp => new InMemoryFileStorage(options, sp.GetService<TimeProvider>()));
        return services;
    }
}
