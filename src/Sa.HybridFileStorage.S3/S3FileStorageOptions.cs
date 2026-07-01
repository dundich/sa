namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Configuration options for the S3 (MinIO-compatible) file storage provider.
/// </summary>
public sealed class S3FileStorageOptions
{
    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <c>"s3"</c>.
    /// </summary>
    public string StorageType { get; init; } = "s3";

    /// <summary>
    /// Gets or sets the basket (container) name. Defaults to <c>"share"</c>.
    /// </summary>
    public string Basket { get; init; } = "share";

    /// <summary>
    /// Gets or sets the S3-compatible endpoint URL (e.g., <c>http://localhost:9000</c>).
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Gets or sets the access key for authenticating with the S3 service.
    /// </summary>
    public required string AccessKey { get; init; }

    /// <summary>
    /// Gets or sets the secret key for authenticating with the S3 service.
    /// </summary>
    public required string SecretKey { get; init; }

    /// <summary>
    /// Gets or sets the name of the S3 bucket to use for file storage.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Gets or sets the AWS region. Defaults to <c>"eu-central-1"</c>.
    /// </summary>
    public string Region { get; init; } = "eu-central-1";

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; init; } = false;
}
