using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;

public class InMemoryFileStorage(ICurrentTimeProvider currentTime) : IHybridFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _storage = new();

    public string StorageType => "sa_mem";

    public bool IsReadOnly => false;

    public async Task<StorageResult> UploadFileAsync(FileMetadataInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[fileStream.Length];
        await fileStream.ReadExactlyAsync(buffer, cancellationToken);

        string path = Path.Combine(metadata.TenantId, metadata.FileName).Replace('\\', '/');

        string fileId = $"{StorageType}://{path}";
        _storage[fileId] = buffer;
        return new StorageResult(fileId, true, StorageType, currentTime.GetUtcNow());
    }

    public Task<Stream> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        if (_storage.TryGetValue(fileId, out var data))
        {
            return Task.FromResult<Stream>(new MemoryStream(data));
        }
        throw new FileNotFoundException("File not found in memory storage.", fileId);
    }

    public Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_storage.TryRemove(fileId, out _));
    }

    public bool CanProcessFileId(string fileId) => fileId.StartsWith(StorageType);
}
