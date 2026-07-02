# Sa.Data.S3

Wrapper over `HttpClient` for working with S3-compatible storage systems (Minio, AWS S3, DigitalOcean Spaces, etc.). Fully self-implemented AWS Signature Version 4 — **no dependencies on AWS SDK or Minio SDK**.

---

## Motivation

This is a fork of https://github.com/teoadal/Storage. Motivation: the [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/welcome.html) (4.x) and [Minio .NET](https://github.com/minio/minio-dotnet) (6.x) clients consumed too much memory. Result: speed is comparable to AWS, while memory consumption is ~150× lower than Minio SDK and ~17× lower than AWS SDK.

---

## Creating a Client

### Without DI

```csharp
var client = new S3BucketClient(new HttpClient(), new S3BucketClientSetupSettings
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123"
});
```

### With DI

```csharp
services.AddSaS3BucketClient(new S3BucketClientSetupSettings
{
    Bucket = "mybucket",
    Endpoint = "http://localhost:9000",
    AccessKey = "ROOTUSER",
    SecretKey = "ChangeMe123",
    TotalRequestTimeout = TimeSpan.FromSeconds(180),
    ConnectionPoolLifetime = TimeSpan.FromMinutes(15),
    HandlerLifetime = Timeout.InfiniteTimeSpan // or TimeSpan.FromHours(2) for periodic handler refresh
});

// Usage:
var client = serviceProvider.GetRequiredService<IS3BucketClient>();
```

---

## Settings

| Property | Description | Default |
|----------|-------------|---------|
| `AccessKey` | S3 access key | *(required)* |
| `SecretKey` | S3 secret key | *(required)* |
| `Bucket` | Bucket name | *(required)* |
| `Endpoint` | S3 storage URL | *(required)* |
| `Region` | Region for SigV4 | `"us-east-1"` |
| `Service` | Service name for SigV4 | `"s3"` |
| `UseHttp2` | Force HTTP/2 | `false` |
| `TotalRequestTimeout` | Per-request timeout | `180 sec` |
| `ConnectionPoolLifetime` | Connection pool lifetime | `15 min` |
| `HandlerLifetime` | HttpClient handler lifetime | `∞` (infinite) |

---

## API

### IBucketOperations

```csharp
public interface IBucketOperations
{
    Task<bool> CreateBucket(CancellationToken ct);
    Task<bool> DeleteBucket(CancellationToken ct);
    Task<bool> DeleteBucket(bool forceDelete, CancellationToken ct); // force: delete all objects before removing bucket
    Task<bool> IsBucketExists(CancellationToken ct);
}
```

### IFileOperations

```csharp
public interface IFileOperations
{
    string BuildFileUrl(string fileName);
    string BuildFileUrl(string fileName, TimeSpan expiration);
    Task DeleteFile(string fileName, CancellationToken ct);
    Task<S3File> GetFile(string fileName, CancellationToken ct);
    Task<Stream> GetFileStream(string fileName, CancellationToken ct);
    Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct);
    Task<bool> IsFileExists(string fileName, CancellationToken ct);
    IAsyncEnumerable<string> List(string? prefix, CancellationToken ct); // with pagination
    Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct);
    Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct); // manual multipart
    Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct);
}
```

### Manual Multipart Upload

For files > 5MB, multipart upload is selected automatically. For manual control:

```csharp
using var uploader = await client.UploadFile("large-file.bin", "application/octet-stream", ct);

uploader.AddPart(chunkData, ct);
uploader.AddPart(chunkData, offset, length, ct); // overload with offset
uploader.AddParts(fullDataStream, ct);
uploader.AddParts(fullByteArray, ct);

if (await uploader.Complete(ct))
{
    Console.WriteLine($"Uploaded {uploader.Written} bytes");
}
else
{
    await uploader.Abort(ct);
}
```

---

## Implementation Details

- **AWS SigV4** — full manual implementation of request signing (SHA256 + HMAC-SHA256 chain)
- **ArrayPool<T>.Shared** — buffer pooling to minimize GC pressure
- **ref struct ValueStringBuilder** — stack-based string builder with zero allocations
- **stackalloc** — wherever possible to avoid heap allocation
- **Buffered XML parser** — efficient reading of S3 responses (ListObjects, Multipart IDs)
- **Pagination** — automatic handling of `IsTruncated` / `NextContinuationToken` in `List()`
- **CancellationToken** — supported in all async operations

---

## License

MIT
