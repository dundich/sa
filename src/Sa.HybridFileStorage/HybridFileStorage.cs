using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;


internal sealed class HybridFileStorage(
    HybridFileStorageContainer container,
    InterceptorContainer interceptors) : IHybridFileStorage
{

    public IEnumerable<IFileStorage> Storages => container.Storages;

    private void EnsureWritable(string basket)
    {
        // The chain, not the raw registry: a type excluded by the basket's order does not
        // participate, so a basket whose chain is empty has no available storage at all —
        // even when excluded storages are registered for it.
        var chain = container.GetBasket(basket);

        if (chain.Count == 0)
        {
            throw new HybridFileStorageNoAvailableException();
        }

        foreach (var storage in chain)
        {
            if (!storage.IsReadOnly)
            {
                return;
            }
        }

        throw new HybridFileStorageWritableException();
    }

    public async Task<StorageResult> UploadAsync(
        string basket,
        UploadFileInput input,
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(basket);

        EnsureWritable(basket);

        return await ExecuteStorageOperationAsync(
            container.GetBasket(basket).Where(c => !c.IsReadOnly),
            async (storage, ct) => await interceptors.ExecuteBeforeUploadAsync(storage, input, fileStream, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.UploadAsync(input, fileStream, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterUploadAsync(storage, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnUploadErrorAsync(storage, e, ct).ConfigureAwait(false),
            // A non-throwing upload result is definitive: the chain stops at the first storage
            // that accepted the file (upload-first-wins).
            static _ => false,
            static () => throw new HybridFileStorageNoAvailableException(),
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        return await ExecuteStorageOperationAsync(
            ReadCandidates(fileId),
            async (storage, ct) => await interceptors.ExecuteBeforeDownloadAsync(storage, fileId, loadStream, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.DownloadAsync(fileId, loadStream, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterDownloadAsync(storage, fileId, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnDownloadErrorAsync(storage, fileId, e, ct).ConfigureAwait(false),
            // `false` means "this storage has no such file" — the probing continues.
            static result => !result,
            static () => throw new HybridFileStorageNoAvailableException(),
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        return await ExecuteStorageOperationAsync(
            ReadCandidates(fileId).Where(c => !c.IsReadOnly),
            async (storage, ct) => await interceptors.ExecuteBeforeDeleteAsync(storage, fileId, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.DeleteAsync(fileId, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterDeleteAsync(storage, fileId, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnDeleteErrorAsync(storage, fileId, e, ct).ConfigureAwait(false),
            static result => !result,
            static () => throw new HybridFileStorageNoAvailableException(),
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        // The same probing as download: `null` means "not here" and the chain keeps going
        // inside the storage type. A lookup has no availability requirement, so zero candidates
        // is a plain `null` rather than an exception.
        return await ExecuteStorageOperationAsync(
            ReadCandidates(fileId),
            static (_, _) => Task.FromResult(true),
            (storage, ct) => storage.GetMetadataAsync(fileId, ct),
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            static metadata => metadata is null,
            static () => (FileMetadata?)null,
            cancellationToken
        ).ConfigureAwait(false);
    }


    /// <summary>
    /// Candidates for a read operation: every storage that recognizes the file ID (the file ID
    /// scheme is what ties a read to a storage type), ordered by the effective type order of the
    /// file's basket — the basket parsed out of the file ID, or the global order when the file ID
    /// does not parse. An order that exists but does not list the recognizing type leaves no
    /// candidates: an excluded type is never probed.
    /// </summary>
    private IEnumerable<IFileStorage> ReadCandidates(string fileId)
    {
        string? basket = FileIdParser.TryParse(fileId, out var parsedBasket, out _, out _, out _)
            ? parsedBasket
            : null;

        return container.OrderCandidates(
            basket,
            container.Storages.Where(c => c.CanProcess(fileId)));
    }

    /// <summary>
    /// Runs an operation over the chain until it produces a definitive answer.
    /// </summary>
    /// <remarks>
    /// An interceptor veto (<paramref name="beforeOperation"/> returning <c>false</c>) skips the
    /// storage without voting. A not-found answer (<paramref name="isNotFound"/>) does not end the
    /// operation: the probing continues with the next storage of the same <see cref="IFileStorage.StorageType"/>,
    /// and the first storage of a different type ends it — the not-found answer is then returned
    /// as is, without an exception. An exception is failover fuel: the next storage is tried
    /// whatever its type, and the error is handed to <paramref name="onError"/>; if no storage
    /// produced an answer at all, the collected errors are thrown as
    /// <see cref="HybridFileStorageAggregateException"/>. No candidates and no errors reach
    /// <paramref name="onUnavailable"/>: the operations throw
    /// <see cref="HybridFileStorageNoAvailableException"/>, the lookup returns its not-found value.
    /// </remarks>
    private static async Task<T> ExecuteStorageOperationAsync<T>(
        IEnumerable<IFileStorage> storages,
        Func<IFileStorage, CancellationToken, Task<bool>> beforeOperation,
        Func<IFileStorage, CancellationToken, Task<T>> operation,
        Func<IFileStorage, T, CancellationToken, Task> afterOperation,
        Func<IFileStorage, Exception, CancellationToken, Task> onError,
        Func<T, bool> isNotFound,
        Func<T> onUnavailable,
        CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();
        string? missedType = null;
        T notFound = default!;

        foreach (var storage in storages)
        {
            // A "not found" from type T keeps the probing inside type T: the first storage of a
            // different type ends the chain, and the accumulated answer is then final.
            if (missedType is not null &&
                !string.Equals(storage.StorageType, missedType, StringComparison.Ordinal))
            {
                break;
            }

            try
            {
                if (!await beforeOperation(storage, cancellationToken).ConfigureAwait(false)) continue;

                var result = await operation(storage, cancellationToken).ConfigureAwait(false);
                await afterOperation(storage, result, cancellationToken).ConfigureAwait(false);

                if (!isNotFound(result))
                {
                    return result;
                }

                missedType ??= storage.StorageType;
                notFound = result;
            }
            catch (Exception e)
            {
                exceptions.Add(e);
                await onError(storage, e, cancellationToken).ConfigureAwait(false);
            }
        }

        // A definitive "not found" beats the errors collected on the way: at least one storage
        // answered, and its answer is "the file is not there".
        if (missedType is not null)
        {
            return notFound;
        }

        if (exceptions.Count > 0)
        {
            throw new HybridFileStorageAggregateException(exceptions);
        }

        return onUnavailable();
    }
}
