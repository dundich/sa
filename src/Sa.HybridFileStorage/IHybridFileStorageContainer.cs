using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorageContainer: IHybridFileStorageContainerConfiguration
{
    IReadOnlyCollection<IFileStorage> Storages { get; }
    string StorageType => string.Join(',', Storages.Select(c => c.StorageType));
    bool IsReadOnly => Storages.All(f => f.IsReadOnly);
    bool CanProcess(string fileId) => Storages.Any(c => c.CanProcess(fileId));

    IEnumerable<IFileStorage> GetStorages(string fileId) => Storages.Where(c => c.CanProcess(fileId));
}
