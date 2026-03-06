namespace Sa.HybridFileStorage.S3;

public sealed class S3FileStorageOptions
{
    public string StorageType { get; init; } = "s3";
    public string? ScopeName { get; init; } = null;

    /// <summary>
    /// http://localhost:9000
    /// </summary>
    public required string Endpoint { get; init; }
    public required string AccessKey { get; init; }
    public required string SecretKey { get; init; }
    public required string Bucket { get; init; }
    public string Region { get; init; } = "eu-central-1";

    public bool IsReadOnly { get; init; } = false;
}
