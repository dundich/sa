using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainer : IHybridFileStorageContainerConfiguration
{
    IReadOnlyCollection<IFileStorage> Storages { get; }
}
