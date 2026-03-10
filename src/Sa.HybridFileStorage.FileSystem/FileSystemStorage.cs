using Sa.HybridFileStorage;
using Sa.HybridFileStorage.Domain;
using Sa.HybridFileStorage.FileSystem;
using System.Globalization;
using System.Runtime.CompilerServices;

internal sealed class FileSystemStorage(
    FileSystemStorageSettings settings,
    TimeProvider? timeProvider = null) : IFileStorage
{
    private const string SchemeSeparator = "://";

    private readonly string _basePath = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(Path.Combine(settings.BasePath, settings.ScopeName)));

    private readonly string _schemePrefix = $"{settings.StorageType}{SchemeSeparator}";
    private readonly string _storageType = settings.StorageType;
    private readonly bool _isReadOnly = settings.IsReadOnly;
    private readonly int _bufferSize = settings.BufferSize;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string ScopeName => settings.ScopeName;
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
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.FileName);
        ArgumentNullException.ThrowIfNull(fileStream);
        EnsureWritable();

        string filename = PathSanitizer.SanitizeRelativePath(metadata.FileName);

        string relativePath = string.Concat(metadata.TenantId.ToString(), "/", filename);
        string filePath = Path.Combine(_basePath, relativePath);

        EnsurePathWithinBase(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            EnsureDirectory(directory);

        await using var fileStreamOutput = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = _bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = 5 * 1024 * 1024
        });

        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken).ConfigureAwait(false);

        return new StorageResult(
            string.Concat(_schemePrefix, relativePath),
            Path.GetFullPath(filePath),
            _storageType,
            _timeProvider.GetUtcNow());
    }

    public async Task<bool> DownloadAsync(
        string fileId,
        Func<Stream, CancellationToken, Task> loadStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        ArgumentNullException.ThrowIfNull(loadStream);

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

            if (fs == null || fs == Stream.Null) return false;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId, nameof(fileId));

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
            .StartsWith(_basePath.AsSpan(), StringComparison.Ordinal);
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

        // Парсинг формата: "storageType://tenantId/filename"
        ReadOnlySpan<char> span = fileId.AsSpan();
        int schemeEnd = span.IndexOf(SchemeSeparator.AsSpan());
        if (schemeEnd == -1)
            return Task.FromResult<FileMetadata?>(null);

        var pathPart = span[(schemeEnd + SchemeSeparator.Length)..];

        // Парсинг "tenantId/filename"
        int slashIndex = pathPart.IndexOf('/');
        if (slashIndex == -1)
            return Task.FromResult<FileMetadata?>(null);

        var tenantSpan = pathPart[..slashIndex];
        var fileNameSpan = pathPart[(slashIndex + 1)..];

        if (!int.TryParse(tenantSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int tenantId))
            return Task.FromResult<FileMetadata?>(null);

        var metadata = new FileMetadata
        {
            FileName = fileNameSpan.ToString(),
            TenantId = tenantId
        };

        return Task.FromResult<FileMetadata?>(metadata);
    }
}
