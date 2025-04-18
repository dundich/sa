using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;

public class InMemoryFileStorage(ICurrentTimeProvider currentTime) : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _storage = new();

    public string StorageType => "mem";

    public bool IsReadOnly => false;

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[fileStream.Length];
        await fileStream.ReadExactlyAsync(buffer, cancellationToken);

        string path = Path.Combine(metadata.TenantId.ToString(), metadata.FileName).Replace('\\', '/');

        string fileId = $"{StorageType}://{path}";
        _storage[fileId] = buffer;
        return new StorageResult(fileId, StorageType, currentTime.GetUtcNow());
    }

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        if (_storage.TryGetValue(fileId, out var data))
        {
            using var mem = new MemoryStream(data);
            await loadStream(mem, cancellationToken);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_storage.TryRemove(fileId, out _));
    }

    public bool CanProcessFileId(string fileId) => fileId.StartsWith(StorageType);
}
