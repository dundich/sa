using Sa.HybridFileStorage.Domain;
using System.Security;

namespace Sa.HybridFileStorage.FileSystem;

internal sealed class FileSystemStorage(
    FileSystemStorageSettings settings,
    TimeProvider? timeProvider = null) : IFileStorage
{

    private readonly string _basePath = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(settings.BasePath ?? throw new ArgumentNullException(nameof(settings.BasePath))));

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string? ScopeName => settings.ScopeName;

    public string StorageType => settings.StorageType ?? "file";

    public bool IsReadOnly => settings.IsReadOnly;

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
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.FileName);
        ArgumentNullException.ThrowIfNull(fileStream);
        EnsureWritable();

        string filename = PathSanitizer.SanitizeRelativePath(metadata.FileName);

        string relativePath = Path.Combine(metadata.TenantId.ToString(), settings.ScopeName ?? string.Empty, filename);

        var filePath = Path.Combine(_basePath, relativePath);

        EnsurePathWithinBase(filePath);

        string? directory = Path.GetDirectoryName(filePath);
        EnsureDirectory(directory);

        await using var fileStreamOutput = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = 10 * 1024 * 1024
        });

        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken);

        var absolutePath = Path.GetFullPath(filePath);

        return new StorageResult(
            FilePathToId(relativePath),
            absolutePath,
            StorageType,
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

        try
        {
            await using var fs = new FileStream(filePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81_920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            await loadStream(fs, cancellationToken);

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

    private string GetFullPath(string fileId)
    {
        var filePath = FileIdToPath(fileId);
        if (!Path.IsPathRooted(filePath))
        {
            filePath = Path.Combine(_basePath, filePath);
        }

        EnsurePathWithinBase(filePath);
        return filePath;
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileId);
        EnsureWritable();

        var filePath = GetFullPath(fileId);

        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            await FileRetryHelper.RetryAsync(
                () => File.Delete(filePath),
                cancellationToken: cancellationToken);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public bool CanProcess(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return false;
        }

        var expectedPrefix = $"{StorageType}://";
        return fileId.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }

    private static void EnsureDirectory(string? dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir!);
        }
    }

    private string FilePathToId(string filePath) => $"{StorageType}://{filePath}";

    private static string FileIdToPath(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException("File ID cannot be null or empty.", nameof(fileId));
        }

        ReadOnlySpan<char> span = fileId.AsSpan();

        int separatorIndex = span.IndexOf("://");
        if (separatorIndex == -1)
        {
            throw new FormatException("Invalid file ID format.");
        }

        ReadOnlySpan<char> filePath = span[(separatorIndex + 3)..]; // +3 for skip "://"

        return filePath.ToString();
    }

    private void EnsurePathWithinBase(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(_basePath, StringComparison.Ordinal))
        {
            throw new SecurityException($"""
                Access denied. Path '{fullPath}' is outside the allowed base directory '{_basePath}'.
                """);
        }

        if (fullPath.Length > _basePath.Length)
        {
            var nextChar = fullPath[_basePath.Length];
            if (nextChar != Path.DirectorySeparatorChar && nextChar != Path.AltDirectorySeparatorChar)
            {
                throw new SecurityException($"""
                    Access denied. Path '{fullPath}' is outside the allowed base directory.
                    """);
            }
        }
    }
}
