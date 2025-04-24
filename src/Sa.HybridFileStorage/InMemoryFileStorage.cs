using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;

public class InMemoryFileStorage(ICurrentTimeProvider currentTimeProvider, bool isReadOnly = false) : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _storage = [];

    public string StorageType => "mem";

    public bool IsReadOnly => isReadOnly;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot perform this operation. The storage is read-only.");
        }
    }

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        EnsureWritable();

        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream, cancellationToken);
        byte[] fileData = memoryStream.ToArray();

        string path = Path.Combine(metadata.TenantId.ToString(), metadata.FileName).Replace('\\', '/');
        string fileId = $"{StorageType}://{path}";

        _storage[fileId] = fileData;
        return new StorageResult(fileId, fileId, StorageType, currentTimeProvider.GetUtcNow());
    }

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        if (_storage.TryGetValue(fileId, out var fileData))
        {
            using var memoryStream = new MemoryStream(fileData);
            await loadStream(memoryStream, cancellationToken);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return Task.FromResult(_storage.TryRemove(fileId, out _));
    }

    public bool CanProcessFileId(string fileId) => fileId.StartsWith(StorageType);
}