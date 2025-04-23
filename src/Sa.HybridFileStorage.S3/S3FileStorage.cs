using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;

namespace Sa.HybridFileStorage.S3;

internal class S3FileStorage(IS3BucketClient client, S3FileStorageOptions options, ICurrentTimeProvider currentTime) : IFileStorage
{
    public string StorageType => options.StorageType;

    public bool IsReadOnly => throw new NotImplementedException();

    public bool CanProcessFileId(string fileId)
    {
        return options.Endpoint.StartsWith(fileId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        await client.DeleteFile(fileId, cancellationToken);
        return true;
    }

    public Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        var file = $"{metadata.TenantId}/{metadata.FileName}";

        await client.UploadFile(file, "", fileStream, cancellationToken);

        return new StorageResult(client.BuildFileUrl(file), StorageType, currentTime.GetUtcNow());
    }
}
