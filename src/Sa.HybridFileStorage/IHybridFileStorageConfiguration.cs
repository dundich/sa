using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;

/// <summary>
/// Defines a configuration pipeline for hybrid file storage interceptors and storage providers.
/// </summary>
public interface IHybridFileStorageConfiguration
{
    /// <summary>
    /// Configures interceptors that can observe or modify upload/download/delete operations.
    /// </summary>
    /// <param name="configure">An action that receives an <see cref="IInterceptorContainer"/> for registering interceptor implementations.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration ConfigureInterceptors(
        Action<IServiceProvider, IInterceptorContainer> configure);

    /// <summary>
    /// Configures storage providers that will participate in the hybrid file storage system.
    /// </summary>
    /// <param name="configure">An action that receives a <see cref="HybridFileStorageContainerConfiguration"/> for registering storage implementations.</param>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration ConfigureStorage(
        Action<IServiceProvider, HybridFileStorageContainerConfiguration> configure);

    /// <summary>
    /// Enables automatic logging of file storage operations through registered interceptors.
    /// </summary>
    /// <returns>The same <see cref="IHybridFileStorageConfiguration"/> instance for fluent chaining.</returns>
    IHybridFileStorageConfiguration AddLogging();
}
