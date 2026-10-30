using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Fluent builder for registering file storage providers into the hybrid container.
/// </summary>
public sealed class HybridFileStorageContainerConfiguration
{
    private readonly Func<IFileStorage, HybridFileStorageContainerConfiguration> _addStorage;

    internal HybridFileStorageContainerConfiguration(Func<IFileStorage, HybridFileStorageContainerConfiguration> addStorage)
    {
        _addStorage = addStorage ?? throw new ArgumentNullException(nameof(addStorage));
    }

    /// <summary>
    /// Adds a file storage provider to the hybrid file storage container.
    /// </summary>
    /// <param name="storage">The storage implementation to register.</param>
    /// <returns>The same instance for chaining.</returns>
    public HybridFileStorageContainerConfiguration AddStorage(IFileStorage storage)
    {
        return _addStorage(storage);
    }
}
