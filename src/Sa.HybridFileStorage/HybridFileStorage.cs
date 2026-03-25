using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.Interceptors;

namespace Sa.HybridFileStorage;


internal sealed class HybridFileStorage(
    IHybridFileStorageContainer container,
    InterceptorContainer interceptors) : IHybridFileStorage
{

    public IReadOnlyCollection<IFileStorage> Storages => container.Storages;

    private void EnsureWritable(string scopeName)
    {
        if (!container.Storages.Any(c => c.ScopeName == scopeName))
        {
            throw new HybridFileStorageNoAvailableException();
        }


        if (Storages.All(f => f.ScopeName == scopeName && f.IsReadOnly))
        {
            throw new HybridFileStorageWritableException();
        }
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput input,
        string scopeName,
        Stream fileStream,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(scopeName, nameof(scopeName));

        EnsureWritable(scopeName);

        return await ExecuteStorageOperationAsync(
            container.Storages.Where(c => !c.IsReadOnly && c.ScopeName == scopeName),
            async (storage, ct) => await interceptors.ExecuteBeforeUploadAsync(storage, input, fileStream, ct),
            async (storage, ct) => await storage.UploadAsync(input, fileStream, ct),
            interceptors.ExecuteAfterUploadAsync,
            interceptors.ExecuteOnUploadErrorAsync,
            cancellationToken
        );
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId, nameof(fileId));

        return await ExecuteStorageOperationAsync(
            CanStorages(fileId),
            async (storage, ct) => await interceptors.ExecuteBeforeDownloadAsync(storage, fileId, loadStream, ct),
            async (storage, ct) => await storage.DownloadAsync(fileId, loadStream, ct),
            async (storage, result, ct) => await interceptors.ExecuteAfterDownloadAsync(storage, fileId, result, ct),
            async (storage, e, ct) => await interceptors.ExecuteOnDownloadErrorAsync(storage, fileId, e, ct),
            cancellationToken
        );
    }

    public async Task<bool> DeleteAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileId, nameof(fileId));

        return await ExecuteStorageOperationAsync(
            CanStorages(fileId).Where(c => !c.IsReadOnly),
            async (storage, ct) => await interceptors.ExecuteBeforeDeleteAsync(storage, fileId, ct),
            async (storage, ct) => await storage.DeleteAsync(fileId, ct),
            async (storage, result, ct) => await interceptors.ExecuteAfterDeleteAsync(storage, fileId, result, ct),
            async (storage, e, ct) => await interceptors.ExecuteOnDeleteErrorAsync(storage, fileId, e, ct),
            cancellationToken
        );
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
                if (!await beforeOperation(storage, cancellationToken)) continue;
                var result = await operation(storage, cancellationToken);
                await afterOperation(storage, result, cancellationToken);
                return result;
            }
            catch (Exception e)
            {
                exceptions.Add(e);
                await onError(storage, e, cancellationToken);
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
        ArgumentNullException.ThrowIfNull(fileId, nameof(fileId));

        foreach (var fs in CanStorages(fileId))
        {
            var meta = await fs.GetMetadataAsync(fileId, cancellationToken);
            if (meta != null) return meta;
        }

        return null;
    }


    internal IEnumerable<IFileStorage> CanStorages(string fileId)
        => container.Storages.Where(c => c.CanProcess(fileId));
}
