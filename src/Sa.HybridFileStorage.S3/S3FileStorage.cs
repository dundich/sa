using Sa.Classes;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;
using Sa.Timing.Providers;
using System.Globalization;

namespace Sa.HybridFileStorage.S3;

internal class S3FileStorage(IS3BucketClient client, S3FileStorageOptions options, ICurrentTimeProvider currentTime) : IFileStorage
{
    private const string DateFormat = "yyyy/MM/dd/HH";

    public string StorageType => options.StorageType;

    public bool IsReadOnly => options.IsReadOnly ?? false;

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Cannot perform this operation. The storage is read-only.");
        }
    }

    public bool CanProcess(string fileId)
    {
        return fileId.StartsWith(StorageType, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> DeleteAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureWritable();

        string filePath = FileIdToPath(fileId);
        await client.DeleteFile(filePath, cancellationToken);
        return true;
    }

    public async Task<bool> DownloadAsync(string fileId, Func<Stream, CancellationToken, Task> loadStream, CancellationToken cancellationToken)
    {
        string filePath = FileIdToPath(fileId);
        using var stream = await client.GetFileStream(filePath, cancellationToken);
        if (stream == null || stream == Stream.Null) return false;
        await loadStream(stream, cancellationToken);
        return true;
    }

    public async Task<StorageResult> UploadAsync(UploadFileInput metadata, Stream fileStream, CancellationToken cancellationToken)
    {
        EnsureWritable();
        await EnsureBucket(cancellationToken);

        var now = currentTime.GetUtcNow();
        var eventTime = now.ToString(DateFormat, CultureInfo.InvariantCulture);

        var filePath = $"{metadata.TenantId}/{eventTime}/{metadata.FileName}";
        var extension = Path.GetExtension(filePath);
        var contentType = MimeTypeMap.GetMimeType(extension);

        await client.UploadFile(filePath, contentType, fileStream, cancellationToken);

        var fileId = FilePathToId(filePath);
        return new StorageResult(fileId, client.BuildFileUrl(filePath), StorageType, now);
    }

    private async Task EnsureBucket(CancellationToken cancellationToken)
    {
        if (!await client.IsBucketExists(cancellationToken))
        {
            await client.CreateBucket(cancellationToken);
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
