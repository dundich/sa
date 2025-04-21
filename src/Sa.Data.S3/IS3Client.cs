
namespace Sa.Data.S3;

public interface IS3Client
{
    string BuildFileUrl(string fileName, TimeSpan expiration);
    Task<bool> CreateBucket(CancellationToken ct);
    Task<bool> DeleteBucket(CancellationToken ct);
    Task DeleteFile(string fileName, CancellationToken ct);
    Task<S3File> GetFile(string fileName, CancellationToken ct);
    Task<Stream> GetFileStream(string fileName, CancellationToken ct);
    Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct);
    Task<bool> IsBucketExists(CancellationToken ct);
    Task<bool> IsFileExists(string fileName, CancellationToken ct);
    IAsyncEnumerable<string> List(string? prefix, CancellationToken ct);
    Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct);
    Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct);
    Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct);
}
