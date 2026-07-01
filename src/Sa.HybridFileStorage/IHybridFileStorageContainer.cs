using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainer : IHybridFileStorageContainerConfiguration
{
    /// <summary>
    /// Gets the collection of registered file storage providers.
    /// </summary>
    IEnumerable<IFileStorage> Storages { get; }
}
