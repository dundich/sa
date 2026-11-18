namespace Sa.Data.S3;

/// <summary>
/// Client for storing and retrieving objects in an S3 bucket.
/// </summary>
public interface IS3BucketClient : IBucketOperations, IFileOperations
{
    /// <summary>
    /// Gets the name of the bucket the client works with.
    /// </summary>
    string Bucket { get; }

    /// <summary>
    /// Gets the S3 endpoint URI.
    /// </summary>
    Uri Endpoint { get; }
}
