namespace Sa.HybridFileStorage.S3;

public class S3FileStorageOptions
{
    public string StorageType { get; set; } = "s3";
    
    /// <summary>
    /// http://localhost:9000
    /// </summary>
    public required string Endpoint { get; set; }
    public required string AccessKey { get; set; }
    public required string SecretKey { get; set; }
    public required string Bucket {  get; set; }
    public string? Region { get; set; }

    public bool? IsReadOnly { get; set; }
}
