namespace Sa.HybridFileStorage;

public sealed class FileMetadata
{
    public required string StorageType { get; init; }
    public required string ScopeName { get; init; }
    public required string FileName { get; init; }
    public int TenantId { get; init; }
}
