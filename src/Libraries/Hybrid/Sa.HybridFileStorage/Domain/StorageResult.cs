namespace Sa.HybridFileStorage.Domain;

/// <summary>
/// Represents the result of a file upload operation.
/// FileId follows URI format: {storage_type}://{basket}/{tenant}/{path_to_resource}[?parameters]
/// Examples:
/// <code>
/// // PostgreSQL storage
/// new StorageResult("pg://files/user_avatars/12345", "https://cdn.example.com/avatars/12345", "pg", DateTimeOffset.Now)
/// 
/// // Amazon S3 storage  
/// new StorageResult("s3://my-bucket/documents/invoice.pdf", "https://my-bucket.s3.amazonaws.com/documents/invoice.pdf", "s3", DateTimeOffset.Now)
/// 
/// // Local file system storage
/// new StorageResult("file:///var/www/uploads/image.png", "/api/files/download/file/var/www/uploads/image.png", "file", DateTimeOffset.Now)
/// </code>
/// </summary>
public sealed record StorageResult
{
    /// <summary>The provider file id.</summary>
    public required string FileId { get; init; }

    /// <summary>The absolute URL or path the file is reachable by.</summary>
    public required string AbsoluteUrl { get; init; }

    /// <summary>The storage type that produced the result.</summary>
    public required string StorageType { get; init; }

    /// <summary>When the file was stored.</summary>
    public DateTimeOffset UploadedAt { get; init; }
}
