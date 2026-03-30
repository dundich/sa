using Sa.HybridFileStorage.Domain;
using System.Collections.Concurrent;
using System.Globalization;

namespace Sa.HybridFileStorage;


public sealed class InMemoryFileStorage(
    InMemoryFileStorageOptions? options = null,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly InMemoryFileStorageOptions _options = options ?? new(string.Empty);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public const string DefaultStorageType = "mem";

    private readonly ConcurrentDictionary<string, byte[]> _storage = [];


    public string Basket => _options.Basket;

    public string StorageType => DefaultStorageType;

    public bool IsReadOnly => _options.IsReadOnly;

    public const string SchemeSeparator = "://";

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

        string path = Path.Combine(Basket, metadata.TenantId.ToString(), metadata.FileName).Replace('\\', '/');
        //"storageType://basket/tenant/filename"
        string fileId = $"{StorageType}{SchemeSeparator}{path}";

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

    public async Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!_storage.ContainsKey(fileId)) return null;

        //parse: "storageType://basket/tenant/filename"
        ReadOnlySpan<char> span = fileId.AsSpan();
        int schemeEnd = span.IndexOf(SchemeSeparator.AsSpan());
        if (schemeEnd == -1)
            return null;

        var pathPart = span[(schemeEnd + SchemeSeparator.Length)..];

        // "tenantId/filename"
        int slashIndex = pathPart.IndexOf('/');
        if (slashIndex == -1)
            return null;

        var scopeSpan = pathPart[..slashIndex];

        var nextSpan = pathPart[(slashIndex + 1)..];
        slashIndex = nextSpan.IndexOf('/');

        var tenantSpan = nextSpan[..slashIndex];
        var fileNameSpan = nextSpan[(slashIndex + 1)..];

        if (!int.TryParse(tenantSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int tenantId))
            return null;

        var metadata = new FileMetadata
        {
            StorageType = StorageType,
            Basket = scopeSpan.ToString(),
            FileName = fileNameSpan.ToString(),
            TenantId = tenantId
        };

        return metadata;
    }
}
