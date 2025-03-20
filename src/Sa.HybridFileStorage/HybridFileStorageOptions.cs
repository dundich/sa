using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public class HybridFileStorageOptions
{
    public List<IHybridFileStorage> Storages { get; set; } = [];
}
