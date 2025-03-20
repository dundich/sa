using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.FileSystemStorage;


public class FileSystemStorage(FileSystemStorageOption options, ICurrentTimeProvider currentTime) : IHybridFileStorage
{
    private readonly string _basePath = options.BasePath;

    public string StorageType => "file";

    public bool IsReadOnly => options.IsReadOnly;

    public async Task<StorageResult> UploadFileAsync(FileMetadataInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        string filePath = Path.Combine(_basePath, metadata.TenantId, metadata.FileName).Replace('\\', '/');

        string? dir = Path.GetDirectoryName(filePath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir!);
        }

        var fileId = new Uri(filePath).AbsolutePath;

        using var fileStreamOutput = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await fileStream.CopyToAsync(fileStreamOutput, cancellationToken);
        return new StorageResult(fileId, Success: true, StorageType, currentTime.GetUtcNow());
    }

    public Task<Stream> DownloadFileAsync(string fileId, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_basePath, fileId);
        if (File.Exists(filePath))
        {
            return Task.FromResult<Stream>(new FileStream(filePath, FileMode.Open, FileAccess.Read));
        }
        throw new FileNotFoundException("File not found in file system storage.", fileId);
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
