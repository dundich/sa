using Sa.Classes;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using System.Runtime.CompilerServices;

namespace Sa.HybridFileStorage.S3;

internal sealed class S3FileStorage(
    IS3BucketClient client,
    S3FileStorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage, IDisposable
{
    private const string SchemeSeparator = "://";

    // The options reach this type only via Setup.AddSaS3FileStorage, whose ValidateOnStart() +
    // IValidateOptions<S3FileStorageOptions> make Basket a non-empty validated word, and the
    // pipeline only hands over a fully validated instance. The previous IsNullOrWhiteSpace
    // fallback to "share" was unreachable.
    private readonly string _pathPrefix = options.Basket;
    private readonly string _schemePrefix = $"{options.StorageType}{SchemeSeparator}";
    private readonly string _storageType = options.StorageType;
    private readonly bool _isReadOnly = options.IsReadOnly;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // One bucket initialization per storage, retried after a failure. A cached Task (the previous
    // design) kept a faulted result — and the first caller's cancellation token — for the lifetime
    // of the process, so a single transient S3 error or one cancelled first upload broke every
    // later upload. The gate serializes the check and is released on failure, so the next upload
    // simply tries again.
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private volatile bool _bucketEnsured;

    public string StorageType => _storageType;
    public bool IsReadOnly => _isReadOnly;
    public string Basket => _pathPrefix;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureWritable()
    {
        if (_isReadOnly)
            HybridFileStorageThrowHelper.ThrowWritableException();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool CanProcess(string? fileId)
    {
        if (string.IsNullOrEmpty(fileId)) return false;
        return fileId.AsSpan().StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        if (!CanProcess(fileId))
            return false;

        var filePath = GetFilePath(fileId);
        await client.DeleteFile(filePath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {

        if (!CanProcess(fileId))
            return false;

        var filePath = GetFilePath(fileId);
        using var stream = await client.GetFileStream(filePath, cancellationToken).ConfigureAwait(false);
        if (stream is null || stream == Stream.Null) return false;

        await loadStream(stream, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(fileStream);

        metadata.Validate();
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        // Оптимизированная сборка пути без лишних аллокаций
        var filePath = string.Concat(_pathPrefix, "/", metadata.TenantId.ToString(), "/", metadata.FileName);

        var extension = Path.GetExtension(filePath);
        var contentType = MimeTypeMap.GetMimeType(extension);

        await client.UploadFile(filePath, contentType, fileStream, cancellationToken).ConfigureAwait(false);

        var fileId = string.Concat(_schemePrefix, filePath);

        return new StorageResult(
            fileId,
            client.BuildFileUrl(filePath),
            _storageType,
            _timeProvider.GetUtcNow());
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        // Fast path: already ensured, skip the gate entirely.
        if (_bucketEnsured)
        {
            return;
        }

        await _bucketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another upload may have created the bucket while this one waited.
            if (_bucketEnsured)
            {
                return;
            }

            if (!await client.IsBucketExists(cancellationToken).ConfigureAwait(false))
            {
                await client.CreateBucket(cancellationToken).ConfigureAwait(false);
            }

            _bucketEnsured = true;
        }
        finally
        {
            // Released on success and on failure: a failed attempt must not poison the storage.
            _bucketGate.Release();
        }
    }

    /// <summary>
    /// Releases the bucket-initialization gate at container shutdown. A directly constructed
    /// (non-DI) instance may be left undisposed: a <see cref="SemaphoreSlim"/> without a wait
    /// handle allocates no OS resource.
    /// </summary>
    public void Dispose() => _bucketGate.Dispose();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetFilePath(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        ReadOnlySpan<char> span = fileId.AsSpan();
        int separatorIndex = span.IndexOf(SchemeSeparator.AsSpan());

        if (separatorIndex == -1)
            HybridFileStorageThrowHelper.ThrowInvalidFileIdFormat();

        return span[(separatorIndex + SchemeSeparator.Length)..].ToString();
    }

    public async Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (!CanProcess(fileId)) return null;

        if (!FileIdParser.TryParse(fileId, FileIdFormat.WithoutTimestamp, out var basket, out var tenantId, out _, out var fileName))
            return null;

        return new FileMetadata
        {
            Basket = basket,
            StorageType = StorageType,
            FileName = fileName,
            TenantId = tenantId
        };
    }
}
