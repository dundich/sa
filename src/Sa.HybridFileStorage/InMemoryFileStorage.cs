using Sa.HybridFileStorage.Domain;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;

/// <summary>
/// An in-memory implementation of <see cref="IFileStorage"/> that stores file data as byte arrays in a <see cref="ConcurrentDictionary{TKey, TValue"/>.
/// Suitable for testing, caching, or small-scale scenarios where persistence is not required.
/// </summary>
public sealed class InMemoryFileStorage(
    InMemoryFileStorageOptions? options = null,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly InMemoryFileStorageOptions _options = options ?? new(string.Empty);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Gets the default storage type identifier for in-memory storage.
    /// </summary>
    public const string DefaultStorageType = "mem";

    private readonly ConcurrentDictionary<string, byte[]> _storage = [];
    private long _totalSizeBytes;

    /// <summary>
    /// Gets the basket (container) name used by this storage instance.
    /// </summary>
    public string Basket => _options.Basket;

    /// <summary>
    /// Gets the storage type identifier (<c>"mem"</c>).
    /// </summary>
    public string StorageType => DefaultStorageType;

    /// <summary>
    /// Gets a value indicating whether this storage instance is read-only.
    /// </summary>
    public bool IsReadOnly => _options.IsReadOnly;

    /// <summary>
    /// Gets the scheme separator used to construct file IDs in the format <c>"storageType://basket/tenant/filename"</c>.
    /// </summary>
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

        metadata.Validate();

        using var memoryStream = new MemoryStream();
        await fileStream.CopyToAsync(memoryStream, cancellationToken)
            .ConfigureAwait(false);
        byte[] fileData = memoryStream.ToArray();

        // Check size limit before inserting
        if (_options.MaxSizeBytes > 0)
        {
            long newSize = Interlocked.Add(ref _totalSizeBytes, fileData.Length);
            if (newSize > _options.MaxSizeBytes)
            {
                // Rollback the addition and throw
                Interlocked.Add(ref _totalSizeBytes, -fileData.Length);
                throw new InvalidOperationException(
                    $"In-memory storage size limit ({_options.MaxSizeBytes} bytes) exceeded.");
            }
        }

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

        if (!CanProcess(fileId))
            return false;

        if (_storage.TryGetValue(fileId, out var fileData))
        {
            using var memoryStream = new MemoryStream(fileData);
            await loadStream(memoryStream, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();


        if (!CanProcess(fileId))
            return Task.FromResult(false);

        if (_storage.TryRemove(fileId, out var fileData))
        {
            Interlocked.Add(ref _totalSizeBytes, -fileData.Length);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public bool CanProcess(string fileId) => fileId.StartsWith(StorageType);

    public async Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!_storage.ContainsKey(fileId)) return null;

        if (!FileIdParser.TryParse(fileId, out var basket, out var tenantId, out _, out var fileName))
            return null;

        var metadata = new FileMetadata
        {
            StorageType = StorageType,
            Basket = basket,
            FileName = fileName,
            TenantId = tenantId
        };

        return metadata;
    }
}
