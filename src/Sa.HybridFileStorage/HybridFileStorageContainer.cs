using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Thread-safe container for multiple <see cref="IFileStorage"/> providers.
/// Uses copy-on-write semantics to allow safe enumeration during concurrent mutations.
/// </summary>
internal sealed class HybridFileStorageContainer(IEnumerable<IFileStorage> storages) : IHybridFileStorageContainer
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<IFileStorage> _storages = [.. storages];

    /// <inheritdoc/>
    public HybridFileStorageContainerConfiguration AddStorage(IFileStorage storage)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_storages.Contains(storage))
            {
                _storages.Add(storage);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return new HybridFileStorageContainerConfiguration(AddStorage);
    }

    /// <inheritdoc/>
    public IEnumerable<IFileStorage> Storages
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                // Snapshot to avoid holding the lock during enumeration
                return [.. _storages];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }
}
