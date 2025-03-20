using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageBuilder
{
    IHybridFileStorageBuilder AddStorage(IHybridFileStorage storage);
    IHybridFileStorage Build();
}
