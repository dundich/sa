using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.FileSystemStorage;

internal class FileSystemStorage(FileSystemStorageOptions options, ICurrentTimeProvider currentTime) : IFileStorage
{
    private readonly string _basePath = options.BasePath;

    public string StorageType => options.StorageType ?? "file";

    public bool IsReadOnly => options.IsReadOnly ?? false;

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        string filePath = Path.Combine(_basePath, metadata.TenantId.ToString(), metadata.FileName).Replace('\\', '/');

        string? dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir!);
        }

        var fileId = new Uri(filePath).AbsolutePath;

        using var fileStreamOutput = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken);
        return new StorageResult(fileId, StorageType, currentTime.GetUtcNow());
    }

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_basePath, fileId);
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
        var filePath = Path.Combine(_basePath, fileId);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public bool CanProcessFileId(string fileId) => fileId.StartsWith(StorageType);
}
