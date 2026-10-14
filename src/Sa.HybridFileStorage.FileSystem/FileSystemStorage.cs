using Sa.Data.TempFolder;
using Sa.HybridFileStorage.Domain;
using System.Runtime.CompilerServices;


namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// fs://share/tenant/filename
/// </summary>
/// <remarks>
/// All file I/O — writing, reading and deleting — is delegated to the keyed
/// <see cref="ITempFolder"/> this storage was registered with (see
/// <see cref="Setup.AddSaFileSystemFileStorage"/>), so the storage no longer opens
/// <c>FileStream</c>s or calls <c>File.Delete</c> itself: path guarding, cleanup activity and
/// retries belong to the temp folder, and this type only maps a file ID to a relative path and
/// shapes the result.
/// </remarks>
internal sealed class FileSystemStorage(
    ITempFolder tempFolder,
    FileSystemStorageOptions options,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private const string SchemeSeparator = "://";

    // The temp folder hands out a fully qualified root; the trim keeps the prefix comparisons
    // below free of a trailing separator.
    private readonly string _root = Path.TrimEndingDirectorySeparator(tempFolder.RootPath);

    private readonly string _basePathScope = Path.TrimEndingDirectorySeparator(
        Path.Combine(tempFolder.RootPath, options.Basket));

    private readonly string _schemePrefix = $"{options.StorageType}{SchemeSeparator}";
    private readonly string _storageType = options.StorageType;
    private readonly bool _isReadOnly = options.IsReadOnly;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Basket => options.Basket;
    public string StorageType => _storageType;
    public bool IsReadOnly => _isReadOnly;

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

        if (!fileId.AsSpan().StartsWith(_schemePrefix.AsSpan(), StringComparison.Ordinal))
            return false;

        return IsPathWithinBase(GetFullPathFast(fileId));
    }

    public async Task<StorageResult> UploadAsync(
        UploadFileInput metadata,
        Stream fileStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        EnsureWritable();

        metadata.Validate();

        string filename = PathSanitizer.SanitizeRelativePath(metadata.FileName);

        // The layout and the file ID stay exactly as before: {Basket}/{tenant}/{file}, joined
        // with forward slashes so the fs:// prefix reads the same on every platform. The temp
        // folder resolves and guards the path against its root.
        string relativePath = string.Concat(Basket, "/", metadata.TenantId.ToString(), "/", filename);

        var (_, absolutePath) = await tempFolder
            .WriteAsync(fileStream, relativePath, cancellationToken)
            .ConfigureAwait(false);

        return new StorageResult(
            FileId: string.Concat(_schemePrefix, relativePath),
            AbsoluteUrl: absolutePath,
            StorageType: _storageType,
            UploadedAt: _timeProvider.GetUtcNow());
    }

    public Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(loadStream);

        if (!CanProcess(fileId))
            return Task.FromResult(false);

        // The temp folder already reads a missing file as `false` and rejects a directory path;
        // a non-existent file therefore keeps the provider's historical "return false" contract.
        return tempFolder.ReadAsync(FileIdToPath(fileId), loadStream, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        EnsureWritable();

        if (!CanProcess(fileId))
            return false;

        try
        {
            return await tempFolder
                .DeleteFileAsync(FileIdToPath(fileId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A transient failure that survived the temp folder's retries reads as "not deleted"
            // here — the same contract the provider had when it deleted files itself.
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetFullPathFast(string fileId)
    {
        var relativePath = FileIdToPath(fileId);
        return Path.Combine(_root, relativePath);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FileIdToPath(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        ReadOnlySpan<char> span = fileId.AsSpan();
        int separatorIndex = span.IndexOf(SchemeSeparator.AsSpan());

        if (separatorIndex == -1)
            HybridFileStorageThrowHelper.ThrowInvalidFileIdFormat();

        return span[(separatorIndex + SchemeSeparator.Length)..].ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsPathWithinBase(string path)
    {
        return Path.GetFullPath(path)
            .AsSpan()
            .StartsWith(_basePathScope.AsSpan(), StringComparison.Ordinal);
    }

    public Task<FileMetadata?> GetMetadataAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (!CanProcess(fileId))
            return Task.FromResult<FileMetadata?>(null);

        if (!FileIdParser.TryParse(fileId, out var basket, out var tenantId, out _, out var fileName))
            return Task.FromResult<FileMetadata?>(null);

        var metadata = new FileMetadata
        {
            StorageType = StorageType,
            Basket = basket,
            FileName = fileName,
            TenantId = tenantId
        };

        return Task.FromResult<FileMetadata?>(metadata);
    }
}
