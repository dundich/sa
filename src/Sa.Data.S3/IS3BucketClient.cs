
namespace Sa.Data.S3;

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
    Task<string?> GetFileUrl(string fileName, TimeSpan expiration, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a new bucket in the S3 storage.
    /// </summary>
    Task<bool> CreateBucket(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an existing bucket from the S3 storage.
    /// </summary>
    Task<bool> DeleteBucket(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a specific file from the bucket.
    /// </summary>
    Task DeleteFile(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves metadata for a specific file in the bucket.
    /// </summary>
    Task<S3File> GetFile(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves a file as a stream for reading or processing.
    /// </summary>
    Task<Stream> GetFileStream(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if a bucket exists in the S3 storage.
    /// </summary>
    Task<bool> IsBucketExists(CancellationToken cancellationToken);

    /// <summary>
    /// Checks if a specific file exists in the bucket.
    /// </summary>
    Task<bool> IsFileExists(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Lists files in the bucket, optionally filtered by a prefix.
    /// </summary>
    IAsyncEnumerable<string> List(string? prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a file to the bucket from a byte array.
    /// </summary>
    Task<bool> UploadFile(string fileName, string contentType, byte[] data, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a file to the bucket and returns upload details.
    /// </summary>
    Task<S3Upload> UploadFile(string fileName, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a file to the bucket from a stream.
    /// </summary>
    Task<bool> UploadFile(string fileName, string contentType, Stream data, CancellationToken cancellationToken);
}