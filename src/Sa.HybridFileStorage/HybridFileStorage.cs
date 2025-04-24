using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;


internal class HybridFileStorage(HybridFileStorageOptions options) : IHybridFileStorage
{
    private readonly List<IFileStorage> _storages = [.. options.Storages];

    public string StorageType => string.Join(',', _storages.Select(c => c.StorageType));

    public bool IsReadOnly => _storages.All(f => f.IsReadOnly);

    public bool CanProcessFileId(string fileId) => _storages.Any(c => c.CanProcessFileId(fileId));

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot upload file. All storage options are read-only.");
        }
    }

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        EnsureWritable();

        var exceptions = new List<Exception>();

        foreach (IFileStorage fileStorage in _storages)
        {
            if (fileStorage.IsReadOnly)
            {
                continue;
            }

            try
            {
                return await fileStorage.UploadFileAsync(metadata, fileStream, cancellationToken);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        throw new AggregateException("Failed to upload file to all available storages.", exceptions);
    }

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        foreach (IFileStorage fileStorage in GetStoragesByFieldId(fileId))
        {
            if (await fileStorage.DownloadFileAsync(fileId, loadStream, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        List<Exception> exceptions = [];

        foreach (IFileStorage fileStorage in GetStoragesByFieldId(fileId))
        {
            try
            {
                if (await fileStorage.DeleteFileAsync(fileId, cancellationToken))
                    return true;
            }
            catch (FileNotFoundException)
            {
                // skip
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count > 0)
        {
            throw new AggregateException("Failed to delete file from all available storages.", exceptions);
        }


        return false;
    }

    private IEnumerable<IFileStorage> GetStoragesByFieldId(string fileId) => _storages.Where(c => c.CanProcessFileId(fileId));
}
