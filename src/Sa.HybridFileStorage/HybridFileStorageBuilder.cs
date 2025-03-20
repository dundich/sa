using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public class HybridFileStorageBuilder: IHybridFileStorageBuilder
{
    private readonly List<IHybridFileStorage> _storages = [];

    public IHybridFileStorageBuilder AddStorage(IHybridFileStorage storage)
    {
        _storages.Add(storage);
        return this;
    }

    public IHybridFileStorage Build()
    {
        var options = new HybridFileStorageOptions
        {
            Storages = _storages
        };

        return new HybridFileStorage(options);
    }
}