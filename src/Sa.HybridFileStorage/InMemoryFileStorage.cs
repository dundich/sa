using Sa.HybridFileStorage.Domain;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;


public sealed class InMemoryFileStorage(
    InMemoryFileStorageOptions? options = null,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly InMemoryFileStorageOptions _options = options ?? new (string.Empty);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public const string DefaultStorageType = "mem";

    private readonly ConcurrentDictionary<string, byte[]> _storage = [];


    public string ScopeName => _options.ScopeName;

    public string StorageType => DefaultStorageType;

    public bool IsReadOnly => _options.IsReadOnly;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new HybridFileStorageWritableException();
        }
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        EnsureWritable();

        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream, cancellationToken);
        byte[] fileData = memoryStream.ToArray();

        string path = Path.Combine(metadata.TenantId.ToString(), metadata.FileName).Replace('\\', '/');
        string fileId = $"{StorageType}://{path}";

        _storage[fileId] = fileData;

        return new StorageResult(fileId, fileId, StorageType, _timeProvider.GetUtcNow());
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        if (_storage.TryGetValue(fileId, out var fileData))
        {
            using var memoryStream = new MemoryStream(fileData);
            await loadStream(memoryStream, cancellationToken);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();
        return Task.FromResult(_storage.TryRemove(fileId, out _));
    }

    public bool CanProcess(string fileId) => fileId.StartsWith(StorageType);

    public Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
