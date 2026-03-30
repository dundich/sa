using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainer : IHybridFileStorageContainerConfiguration
{
    IEnumerable<IFileStorage> Storages { get; }
}
