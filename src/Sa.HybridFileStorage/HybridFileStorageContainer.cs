using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

internal sealed class HybridFileStorageContainer(IEnumerable<IFileStorage> storages) : IHybridFileStorageContainer
{
    private readonly List<IFileStorage> _storages = [.. storages];

    public IHybridFileStorageContainerConfiguration AddStorage(IFileStorage storage)
    {
        _storages.Add(storage);
        return this;
    }

    public IReadOnlyCollection<IFileStorage> Storages => _storages;
}
