namespace Sa.HybridFileStorage.Domain;

/// <summary>
/// Represents the result of a file upload operation.
/// FileId follows URI format: {storage_type}://{path_to_resource}[?parameters]
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
/// <param name="FileId">Unique file identifier in URI format: {storage_type}://{path}[?params]</param>
/// <param name="AbsoluteUrl">Publicly accessible URL for downloading the file</param>
/// <param name="StorageType">Type of storage backend used ("pg", "s3", "file", "azure")</param>
/// <param name="UploadedAt">Timestamp when the file was uploaded</param>
public sealed record StorageResult(string FileId, string AbsoluteUrl, string StorageType, DateTimeOffset UploadedAt);
