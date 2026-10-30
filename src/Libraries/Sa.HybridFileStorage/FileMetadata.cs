namespace Sa.HybridFileStorage;

/// <summary>
/// Represents metadata for a file stored in the hybrid file storage system.
/// </summary>
public sealed class FileMetadata
{
    /// <summary>
    /// Gets or sets the basket (container) name where the file is stored.
    /// </summary>
    public required string Basket { get; init; }

    /// <summary>
    /// Gets or sets the file name.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Gets or sets the tenant identifier associated with the file.
    /// </summary>
    public int TenantId { get; init; }

    /// <summary>
    /// Gets the storage type identifier (e.g., "fs", "s3", "pg").
    /// </summary>
    public required string StorageType { get; init; }
}
