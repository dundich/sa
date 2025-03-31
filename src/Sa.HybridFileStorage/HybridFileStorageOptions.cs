using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public class HybridFileStorageOptions
{
    public List<IFileStorage> Storages { get; set; } = [];
}
