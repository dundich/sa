using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;


internal class HybridFileStorage(HybridFileStorageOptions options) : IHybridFileStorage
{
    private readonly List<IHybridFileStorage> _storages = [.. options.Storages];

    public string StorageType => string.Join(',', _storages.Select(c => c.StorageType));

    public bool IsReadOnly => _storages.All(f => f.IsReadOnly);

    public bool CanProcessFileId(string fileId) => _storages.Any(c => c.CanProcessFileId(fileId));

    public async Task<StorageResult> UploadFileAsync(FileMetadataInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot upload file. All storage options are read-only.");
        }

        var exceptions = new List<Exception>();

        foreach (IHybridFileStorage fileStorage in _storages)
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

    public async Task<Stream> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        foreach (IHybridFileStorage fileStorage in GetStoragesByFieldId(fileId))
        {
            Stream stream = await fileStorage.DownloadFileAsync(fileId, cancellationToken);
            if (stream is not null)
            {
                return stream;
            }
        }

        throw new FileNotFoundException($"File with ID '{fileId}' not found in any storage.");
    }

 
    public async Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot delete file. All storage options are read-only.");
        }

        List<Exception> exceptions = [];

        foreach (IHybridFileStorage fileStorage in GetStoragesByFieldId(fileId))
        {
            try
            {
                return await fileStorage.DeleteFileAsync(fileId, cancellationToken);
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

    private IEnumerable<IHybridFileStorage> GetStoragesByFieldId(string fileId) => _storages.Where(c => c.CanProcessFileId(fileId));
}
