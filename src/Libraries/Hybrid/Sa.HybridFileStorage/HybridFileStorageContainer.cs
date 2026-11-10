using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

/// <summary>
/// Thread-safe container for multiple <see cref="IFileStorage"/> providers.
/// Uses copy-on-write semantics to allow safe enumeration during concurrent mutations.
/// </summary>
internal sealed class HybridFileStorageContainer : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<IFileStorage> _storages;
    private readonly StorageTypeOrdering _ordering;

    // One configuration wrapper for the lifetime of the container: AddStorage hands the same
    // instance back on every call, as its contract promises.
    private readonly HybridFileStorageContainerConfiguration _configuration;

    public HybridFileStorageContainer(
        IEnumerable<IFileStorage> storages,
        StorageTypeOrdering? ordering = null)
    {
        ArgumentNullException.ThrowIfNull(storages);

        _storages = [.. storages];
        _ordering = ordering ?? StorageTypeOrdering.Empty;
        _configuration = new HybridFileStorageContainerConfiguration(AddStorage);
    }

    /// <summary>
    /// Adds a storage and returns the container's configuration for continued chaining. Adding a
    /// storage that is already present (by reference) is a no-op.
    /// </summary>
    public HybridFileStorageContainerConfiguration AddStorage(IFileStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

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

        return _configuration;
    }

    /// <summary>
    /// Gets a snapshot of the registered storages in registration order.
    /// </summary>
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

    /// <summary>
    /// Gets the basket's failover chain: the storages registered for the basket in registration
    /// order, reordered — and filtered — by the effective storage-type order of the basket.
    /// A registered type excluded by that order is not part of the chain; an order entry without
    /// a registered storage is skipped.
    /// </summary>
    /// <param name="basket">The basket (scope) name.</param>
    public IReadOnlyList<IFileStorage> GetBasket(string basket)
    {
        ArgumentNullException.ThrowIfNull(basket);

        return [.. _ordering.Apply(basket, Storages.Where(storage => storage.Basket == basket))];
    }

    /// <summary>
    /// Applies the effective storage-type order of a basket to an arbitrary candidate set.
    /// Read operations pick their candidates by <c>CanProcess(fileId)</c> rather than by
    /// <c>Basket</c>, but the type-order filter of the file's basket still governs them.
    /// </summary>
    /// <param name="basket">The basket the order is taken from, or <c>null</c> when it is unknown — only the global list applies then.</param>
    /// <param name="candidates">The candidates in registration order.</param>
    public IEnumerable<IFileStorage> OrderCandidates(string? basket, IEnumerable<IFileStorage> candidates)
        => _ordering.Apply(basket, candidates);

    /// <summary>Releases the reader/writer lock.</summary>
    public void Dispose() => _lock.Dispose();
}
