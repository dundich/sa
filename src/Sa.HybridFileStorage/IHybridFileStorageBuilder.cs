using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageBuilder
{
    IHybridFileStorageBuilder AddStorage(IFileStorage storage);
    IHybridFileStorage Build();
}
