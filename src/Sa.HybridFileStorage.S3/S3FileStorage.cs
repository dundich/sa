using Sa.Classes;
using Sa.Data.S3;
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
    private int _bucketEnsured; // 0 = not ensured, 1 = ensured (thread-safe via Interlocked)

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

    private async ValueTask EnsureBucketAsync(CancellationToken cancellationToken)
    {
        // Lock-free проверка: проверяем bucket только один раз за время жизни экземпляра
        if (Volatile.Read(ref _bucketEnsured) == 0)
        {
            if (await client.IsBucketExists(cancellationToken).ConfigureAwait(false))
            {
                Volatile.Write(ref _bucketEnsured, 1);
                return;
            }

            await client.CreateBucket(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _bucketEnsured, 1);
        }
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

        var filePath = GetFilePath(fileId);
        ReadOnlySpan<char> pathSpan = filePath.AsSpan();

        // Парсинг пути: "scope/tenantId/filename"
        int firstSlash = pathSpan.IndexOf('/');
        if (firstSlash == -1) return null;

        var afterScope = pathSpan[(firstSlash + 1)..];
        int secondSlash = afterScope.IndexOf('/');
        if (secondSlash == -1) return null;

        var tenantSpan = afterScope[..secondSlash];
        var fileNameSpan = afterScope[(secondSlash + 1)..];

        if (!int.TryParse(tenantSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int tenantId))
            return null;

        return new FileMetadata
        {
            Basket = Basket,
            StorageType = StorageType,
            FileName = fileNameSpan.ToString(),
            TenantId = tenantId
        };
    }
}
