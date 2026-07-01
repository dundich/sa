using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Thread-safe container for multiple <see cref="IFileStorage"/> providers.
/// </summary>
public interface IHybridFileStorageContainer
{
    /// <summary>
    /// Gets the collection of registered file storage providers.
    /// </summary>
    IEnumerable<IFileStorage> Storages { get; }

    /// <summary>
    /// Adds a file storage provider to the hybrid container.
    /// </summary>
    /// <param name="storage">The storage implementation to register.</param>
    /// <returns>The same instance for chaining.</returns>
    HybridFileStorageContainerConfiguration AddStorage(IFileStorage storage);
}
