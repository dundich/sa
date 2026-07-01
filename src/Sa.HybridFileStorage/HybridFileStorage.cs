using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;


internal sealed class HybridFileStorage(
    IHybridFileStorageContainer container,
    InterceptorContainer interceptors) : IHybridFileStorage
{

    public IEnumerable<IFileStorage> Storages => container.Storages;

    private void EnsureWritable(string basket)
    {
        bool hasWritable = false;
        bool hasAny = false;

        foreach (var storage in container.Storages)
        {
            if (storage.Basket == basket)
            {
                hasAny = true;
                if (!storage.IsReadOnly)
                {
                    hasWritable = true;
                }
            }
        }

        if (!hasAny)
        {
            throw new HybridFileStorageNoAvailableException();
        }

        if (!hasWritable)
        {
            throw new HybridFileStorageWritableException();
        }
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
            container.Storages.Where(c => !c.IsReadOnly && c.Basket == basket),
            async (storage, ct) => await interceptors.ExecuteBeforeUploadAsync(storage, input, fileStream, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.UploadAsync(input, fileStream, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterUploadAsync(storage, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnUploadErrorAsync(storage, e, ct).ConfigureAwait(false),
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
            CanProcess(fileId),
            async (storage, ct) => await interceptors.ExecuteBeforeDownloadAsync(storage, fileId, loadStream, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.DownloadAsync(fileId, loadStream, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterDownloadAsync(storage, fileId, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnDownloadErrorAsync(storage, fileId, e, ct).ConfigureAwait(false),
            cancellationToken
        ).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        return await ExecuteStorageOperationAsync(
            CanProcess(fileId).Where(c => !c.IsReadOnly),
            async (storage, ct) => await interceptors.ExecuteBeforeDeleteAsync(storage, fileId, ct).ConfigureAwait(false),
            async (storage, ct) => await storage.DeleteAsync(fileId, ct).ConfigureAwait(false),
            async (storage, result, ct) => await interceptors.ExecuteAfterDeleteAsync(storage, fileId, result, ct).ConfigureAwait(false),
            async (storage, e, ct) => await interceptors.ExecuteOnDeleteErrorAsync(storage, fileId, e, ct).ConfigureAwait(false),
            cancellationToken
        ).ConfigureAwait(false);
    }


    private static async Task<T> ExecuteStorageOperationAsync<T>(
        IEnumerable<IFileStorage> storages,
        Func<IFileStorage, CancellationToken, Task<bool>> beforeOperation,
        Func<IFileStorage, CancellationToken, Task<T>> operation,
        Func<IFileStorage, T, CancellationToken, Task> afterOperation,
        Func<IFileStorage, Exception, CancellationToken, Task> onError,
        CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();

        foreach (var storage in storages)
        {
            try
            {
                if (!await beforeOperation(storage, cancellationToken).ConfigureAwait(false)) continue;
                var result = await operation(storage, cancellationToken).ConfigureAwait(false);
                await afterOperation(storage, result, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception e)
            {
                exceptions.Add(e);
                await onError(storage, e, cancellationToken).ConfigureAwait(false);
            }
        }

        if (exceptions.Count > 0)
        {
            throw new HybridFileStorageAggregateException(exceptions);
        }

        throw new HybridFileStorageNoAvailableException();
    }

    public async Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId);

        foreach (var fs in container.Storages)
        {
            var meta = await fs.GetMetadataAsync(fileId, cancellationToken).ConfigureAwait(false);
            if (meta != null) return meta;
        }

        return null;
    }


    internal IEnumerable<IFileStorage> CanProcess(string fileId)
        => container.Storages.Where(c => c.CanProcess(fileId));
}
