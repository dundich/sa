using Sa.HybridFileStorage.Domain;
using System.Collections.Concurrent;

namespace Sa.HybridFileStorage;

/// <summary>
/// An in-memory implementation of <see cref="IFileStorage"/> that stores file data as byte arrays in a <c>ConcurrentDictionary&lt;string, byte[]&gt;</c>.
/// Suitable for testing, caching, or small-scale scenarios where persistence is not required.
/// </summary>
public sealed class InMemoryFileStorage(
    InMemoryFileStorageOptions? options = null,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private readonly InMemoryFileStorageOptions _options = options ?? new();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Gets the default storage type identifier for in-memory storage.
    /// </summary>
    public const string DefaultStorageType = "mem";

    private readonly ConcurrentDictionary<string, byte[]> _storage = [];

    // Guards the read-check-write sequence of an upload and the remove-and-subtract of a delete,
    // so the dictionary and _totalSizeBytes always agree. Reads (download, metadata) stay lock-free.
    private readonly Lock _gate = new();

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

    // The scheme prefix, separator included: a bare StartsWith("mem") would also claim ids of an
    // unrelated scheme such as "memory://" (or a name like "member.txt").
    private static readonly string SchemePrefix = $"{DefaultStorageType}{SchemeSeparator}";

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new HybridFileStorageWritableException();
        }
    }

    /// <summary>Stores a copy of the stream under a new file id.</summary>
    /// <param name="metadata">The upload metadata.</param>
    /// <param name="fileStream">The content to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored file identity.</returns>
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

        string path = Path.Combine(Basket, metadata.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata.FileName).Replace('\\', '/');
        //"storageType://basket/tenant/filename"
        string fileId = $"{StorageType}{SchemeSeparator}{path}";

        // Check the size limit and insert under one gate. A re-upload replaces the payload under
        // the same file ID, so only the delta is charged: charging the full length again would
        // make repeated uploads of one file trip the limit while a single file's worth of data is
        // held. The gate makes the read-check-write sequence atomic, so two concurrent uploads of
        // one file ID cannot both charge the full payload, and a concurrent delete cannot subtract
        // a size this upload has already accounted for.
        lock (_gate)
        {
            long delta = fileData.Length;

            if (_storage.TryGetValue(fileId, out byte[]? previous))
            {
                delta -= previous.LongLength;
            }

            long newSize = _totalSizeBytes + delta;

            if (delta > 0 && _options.MaxSizeBytes > 0 && newSize > _options.MaxSizeBytes)
            {
                throw new InvalidOperationException(
                    $"In-memory storage size limit ({_options.MaxSizeBytes} bytes) exceeded.");
            }

            _storage[fileId] = fileData;
            _totalSizeBytes = newSize;
        }

        return new StorageResult
        {
            FileId = fileId,
            AbsoluteUrl = fileId,
            StorageType = StorageType,
            UploadedAt = _timeProvider.GetUtcNow(),
        };
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();


        if (!CanProcess(fileId))
            return Task.FromResult(false);

        // Same gate as the upload: removing the payload and subtracting its size must be one step,
        // and it must not interleave with a re-upload of the same ID.
        lock (_gate)
        {
            if (_storage.TryRemove(fileId, out var fileData))
            {
                _totalSizeBytes -= fileData.Length;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public bool CanProcess(string? fileId)
        => !string.IsNullOrEmpty(fileId)
            && fileId.AsSpan().StartsWith(SchemePrefix.AsSpan(), StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<FileMetadata?> GetMetadataAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (!_storage.ContainsKey(fileId)) return null;

        if (!FileIdParser.TryParse(fileId, FileIdFormat.WithoutTimestamp, out var basket, out var tenantId, out _, out var fileName))
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
