using Sa.Classes;
using Sa.Data.S3;
using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Sa.HybridFileStorage.S3;

internal sealed class S3FileStorage(
    IS3BucketClient client,
    S3FileStorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private const string DefaultBasket = "share";
    private const string SchemeSeparator = "://";

    private readonly string _pathPrefix = string.IsNullOrWhiteSpace(options.Basket)
        ? DefaultBasket
        : options.Basket;
    private readonly string _schemePrefix = $"{options.StorageType}{SchemeSeparator}";
    private readonly string _storageType = options.StorageType;
    private readonly bool _isReadOnly = options.IsReadOnly;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Async lazy initialization for bucket ensure — prevents concurrent CreateBucket calls
    private volatile Task _ensureBucketTask;

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
        var filePath = GetFilePath(fileId);
        await client.DeleteFile(filePath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
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
        // Fast path: if already ensured, skip entirely
        var task = _ensureBucketTask;
        if (task is not null)
        {
            await task.ConfigureAwait(false);
            return;
        }

        // Slow path: create the task that will ensure the bucket
        var createdTask = EnsureBucketCoreAsync(cancellationToken);

        // CompareExchange ensures only one task survives — others will await the winner
        var comparison = Interlocked.CompareExchange(ref _ensureBucketTask, createdTask, null);
        if (comparison is not null)
        {
            // Another thread won the race — await its task and discard ours
            await createdTask.ConfigureAwait(false);
            await comparison.ConfigureAwait(false);
            return;
        }

        // We won — execute and let callers await the result
        await createdTask.ConfigureAwait(false);
    }

    private async Task EnsureBucketCoreAsync(CancellationToken cancellationToken)
    {
        if (await client.IsBucketExists(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await client.CreateBucket(cancellationToken).ConfigureAwait(false);
    }

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

        if (!FileIdParser.TryParse(fileId, out var basket, out var tenantId, out _, out var fileName))
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
