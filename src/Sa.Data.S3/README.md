# Sa.Data.S3

This is a fork of https://github.com/teoadal/Storage , which is a wrapper around HttpClient for working with S3-compatible storage. It offers performance comparable to MinIO while consuming almost 200 times less memory than the AWS SDK client.

## Creating a Client

To interact with an S3-compatible storage system, you need to create a client using the provided configuration. Below is a detailed explanation of the code snippet and its parameters.

```csharp

var client = new S3BucketClient(new HttpClient(), new S3BucketClientSettings
{
   Bucket = "mybucket",
   Endpoint = "http://localhost:9000",
   AccessKey = "ROOTUSER",
   SecretKey = "ChangeMe123"
};

```

## IS3BucketClient

```csharp
/// <summary>
/// The IS3BucketClient interface provides methods for managing S3-compatible storage buckets and files.
/// Key functionalities include:
/// - File URL generation (direct and time-limited).
/// - Bucket management (create, delete, existence checks).
/// - File management (upload, download, delete, existence checks, listing).
/// - Support for asynchronous operations with CancellationToken for graceful cancellation.
/// 
/// Use cases:
/// - Generating file URLs for direct or temporary access.
/// - Creating and deleting buckets.
/// - Uploading, downloading, and deleting files.
/// - Checking bucket and file existence.
/// - Listing files in a bucket with optional prefix filtering.
/// </summary>
public interface IS3BucketClient
{
    /// <summary>
    /// Generates a direct URL for a file.
    /// </summary>
    string BuildFileUrl(string fileName);

    /// <summary>
    /// Generates a time-limited URL for a file with an expiration duration.
    /// </summary>
    string BuildFileUrl(string fileName, TimeSpan expiration);

    /// <summary>
    /// Asynchronously retrieves a time-limited URL for a file.
    /// </summary>
    Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken ct);

    /// <summary>
    /// Creates a new bucket in the S3 storage.
    /// </summary>
    Task<bool> CreateBucket(CancellationToken ct);

    /// <summary>
    /// Deletes an existing bucket from the S3 storage.
    /// </summary>
    Task<bool> DeleteBucket(CancellationToken ct);

    /// <summary>
    /// Deletes a specific file from the bucket.
    /// </summary>
    Task DeleteFile(string fileName, CancellationToken ct);

    /// <summary>
    /// Retrieves metadata for a specific file in the bucket.
    /// </summary>
    Task<S3File> GetFile(string fileName, CancellationToken ct);

    /// <summary>
    /// Retrieves a file as a stream for reading or processing.
    /// </summary>
    Task<Stream> GetFileStream(string fileName, CancellationToken ct);

    /// <summary>
    /// Checks if a bucket exists in the S3 storage.
    /// </summary>
    Task<bool> IsBucketExists(CancellationToken ct);

    /// <summary>
    /// Checks if a specific file exists in the bucket.
    /// </summary>
    Task<bool> IsFileExists(string fileName, CancellationToken ct);

    /// <summary>
    /// Lists files in the bucket, optionally filtered by a prefix.
    /// </summary>
    IAsyncEnumerable<string> List(string? prefix, CancellationToken ct);

    /// <summary>
    /// Uploads a file to the bucket from a byte array.
    /// </summary>
    Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken ct);

    /// <summary>
    /// Uploads a file to the bucket and returns upload details.
    /// </summary>
    Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken ct);

    /// <summary>
    /// Uploads a file to the bucket from a stream.
    /// </summary>
    Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken ct);
}
```