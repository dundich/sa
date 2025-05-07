using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainerConfiguration
{
    IHybridFileStorageContainerConfiguration AddStorage(IFileStorage storage);
}
