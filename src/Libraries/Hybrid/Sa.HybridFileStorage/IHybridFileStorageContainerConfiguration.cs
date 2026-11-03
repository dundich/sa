using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainerConfiguration
{
    /// <summary>
    /// Adds a file storage provider to the hybrid file storage container.
    /// </summary>
    /// <param name="storage">The storage implementation to register.</param>
    /// <returns>The same <see cref="IHybridFileStorageContainerConfiguration"/> instance for chaining.</returns>
    IHybridFileStorageContainerConfiguration AddStorage(IFileStorage storage);
}
