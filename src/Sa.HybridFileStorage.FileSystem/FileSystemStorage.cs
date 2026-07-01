using Sa.HybridFileStorage.Domain;
using System.Runtime.CompilerServices;


namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// fs://share/tenant/filename
/// </summary>
internal sealed class FileSystemStorage(
    FileSystemStorageSettings settings,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private const string SchemeSeparator = "://";

    private readonly string _basePath = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(settings.BasePath));

    private readonly string _basePathScope = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Path.Combine(settings.BasePath, settings.Basket)));

    private readonly string _schemePrefix = $"{settings.StorageType}{SchemeSeparator}";
    private readonly string _storageType = settings.StorageType;
    private readonly bool _isReadOnly = settings.IsReadOnly;
    private readonly int _bufferSize = settings.BufferSize;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string Basket => settings.Basket;
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

        string relativePath = string.Concat(Basket, "/", metadata.TenantId.ToString(), "/", filename);
        string filePath = Path.Combine(_basePath, relativePath);

        EnsurePathWithinBase(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            EnsureDirectory(directory);

        // Smart preallocation: use actual length when available, fall back to 0 for unknown sizes
        long preallocationSize = 1024 * 1024;
        if (fileStream.CanSeek && fileStream.Length > 0 && fileStream.Length <= int.MaxValue)
        {
            preallocationSize = fileStream.Length;
        }

        await using var fileStreamOutput = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = _bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = (int)preallocationSize
        });

        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken).ConfigureAwait(false);

        return new StorageResult(
            FileId: string.Concat(_schemePrefix, relativePath),
            AbsoluteUrl: Path.GetFullPath(filePath),
            StorageType: _storageType,
            UploadedAt: _timeProvider.GetUtcNow());
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(loadStream);


        if (!CanProcess(fileId))
            return false;

        string filePath = GetFullPath(fileId);
        EnsurePathWithinBase(filePath);

        try
        {
            await using var fs = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = _bufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            await loadStream(fs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetFullPath(string fileId)
    {
        var filePath = FileIdToPath(fileId);
        return Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(_basePath, filePath);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetFullPathFast(string fileId)
    {
        var relativePath = FileIdToPath(fileId);
        return Path.Combine(_basePath, relativePath);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        EnsureWritable();

        if (!CanProcess(fileId))
            return false;

        var filePath = GetFullPath(fileId);
        EnsurePathWithinBase(filePath);

        if (!File.Exists(filePath))
            return false;

        try
        {
            await FileRetryHelper.RetryAsync(
                () => File.Delete(filePath),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureDirectory(string? dir)
    {
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsurePathWithinBase(string path)
    {
        if (!IsPathWithinBase(path))
        {
            HybridFileStorageThrowHelper.ThrowSecurityException(path, _basePath);
        }
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
