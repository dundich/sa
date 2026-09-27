using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the hybrid storage was already registered in this collection.</exception>
    /// <remarks>
    /// Registration is not additive: a second call would <c>TryAdd</c> the
    /// <see cref="IHybridFileStorage"/> factory into a collection that already has one, so the new
    /// factory — together with every storage and interceptor it configures — would be silently
    /// discarded. Throwing names the mistake instead.
    /// </remarks>
    public static IServiceCollection AddSaHybridFileStorage(
        this IServiceCollection services,
        Action<IHybridFileStorageConfiguration>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(IHybridFileStorage)))
        {
            throw new InvalidOperationException(
                "AddSaHybridFileStorage has already been registered in this service collection. " +
                "A second call would be ignored, so the storages and interceptors configured by it " +
                "would be silently lost. Configure everything in a single call, or call it once and " +
                "register the providers with their own Add... methods.");
        }

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
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <c>null</c>.</exception>
    public static IServiceCollection AddSaInMemoryFileStorage(
        this IServiceCollection services,
        InMemoryFileStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        options ??= new();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFileStorage, InMemoryFileStorage>(
            sp => new InMemoryFileStorage(options, sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Registers the in-memory file storage provider as part of the hybrid file storage configuration
    /// pipeline, enabling it to participate in the hybrid container and its interceptors.
    /// </summary>
    /// <param name="configuration">The hybrid file storage configuration pipeline.</param>
    /// <param name="options">Optional configuration options for the in-memory storage. If <c>null</c>, a default instance is used.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is <c>null</c>.</exception>
    /// <remarks>
    /// The provider is created by the container and registered as an <see cref="IFileStorage"/>,
    /// exactly as the service-collection overload does. It therefore appears in
    /// <c>sp.GetServices&lt;IFileStorage&gt;()</c> and is disposed with the provider, and the very
    /// same instance is what the hybrid container receives. Constructing it with <c>new</c> inside
    /// the deferred callback, as this overload used to, bypassed the container entirely.
    /// </remarks>
    public static IHybridFileStorageConfiguration AddSaInMemoryFileStorage(
        this IHybridFileStorageConfiguration configuration,
        InMemoryFileStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options ??= new();

        // HybridStorageBuilder is the only implementation of the interface; the fallback keeps a
        // hypothetical external implementation working, at the cost of requiring the provider to
        // have been registered separately as an IFileStorage.
        if (configuration is HybridStorageBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.TryAddSingleton(TimeProvider.System);
                services.AddSingleton<IFileStorage, InMemoryFileStorage>(
                    sp => new InMemoryFileStorage(options, sp.GetRequiredService<TimeProvider>()));
            });

            // Adding the resolved instance is a no-op when the container has already picked it up
            // from sp.GetServices<IFileStorage>() — HybridFileStorageContainer de-duplicates by
            // reference — and it keeps the pipeline's intent explicit.
            return builder.ConfigureStorage((sp, container) =>
                container.AddStorage(sp.GetServices<IFileStorage>()
                    .First(s => s is InMemoryFileStorage)));
        }

        return configuration.ConfigureStorage((sp, container) =>
            container.AddStorage(sp.GetRequiredService<IFileStorage>()));
    }
}
