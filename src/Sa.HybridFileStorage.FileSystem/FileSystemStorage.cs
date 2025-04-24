using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.FileSystem;

internal class FileSystemStorage(FileSystemStorageOptions options, ICurrentTimeProvider currentTime) : IFileStorage
{
    private readonly string _basePath = Path.TrimEndingDirectorySeparator(options.BasePath);

    public string StorageType => options.StorageType ?? "file";

    public bool IsReadOnly => options.IsReadOnly ?? false;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot perform this operation. The storage is read-only.");
        }
    }

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        EnsureWritable();

        string filePath = $"{_basePath}/{metadata.TenantId}/{metadata.FileName.Replace('\\', '/')}";

        string? dir = Path.GetDirectoryName(filePath);
        EnsureDirectory(dir);

        using var fileStreamOutput = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken);

        var fileId = FilePathToId(filePath);
        var fileAbsolute = Path.GetFullPath(filePath);

        return new StorageResult(fileId, fileAbsolute, StorageType, currentTime.GetUtcNow());
    }

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        var filePath = FileIdToPath(fileId);
        if (File.Exists(filePath))
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            await loadStream(fs, cancellationToken);
            return true;
        }
        return false;
    }

    public Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        var filePath = FileIdToPath(fileId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public bool CanProcessFileId(string fileId) 
        => fileId.StartsWith(FilePathToId(_basePath));

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
}
