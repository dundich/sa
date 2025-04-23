using Sa.Classes;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Globalization;

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

    public async Task<bool> DownloadFileAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        using var stream = await client.GetFileStream(fileId, cancellationToken);
        if (stream == null || stream == Stream.Null) return false;
        await loadStream(stream, cancellationToken);
        return true;
    }

    public async Task<StorageResult> UploadFileAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        await EnsureBucket(cancellationToken);

        var now = currentTime.GetUtcNow();
        var eventTime = now.ToString("yyyy/MM/dd/HH", CultureInfo.InvariantCulture);

        var file = $"{metadata.TenantId}/{eventTime}/{metadata.FileName}";
        var extension = Path.GetExtension(file);
        var contentType = MimeTypeMap.GetMimeType(extension);

        await client.UploadFile(file, contentType, fileStream, cancellationToken);

        return new StorageResult(file, client.BuildFileUrl(file), StorageType, now);
    }

    private async Task EnsureBucket(CancellationToken cancellationToken)
    {
        if (!await client.IsBucketExists(cancellationToken))
        {
            await client.CreateBucket(cancellationToken);
        }
    }
}
