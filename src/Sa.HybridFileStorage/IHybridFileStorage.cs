using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage;

public interface IHybridFileStorage
{
    bool IsReadOnly { get; }
    string StorageType { get; }

    bool CanProcessFileId(string fileId);
    Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken);
    Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken);
    Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken);
}
