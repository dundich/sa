namespace Sa.HybridFileStorage;

public sealed class FileMetadata
{
    public required string FileName { get; init; }
    public int TenantId { get; init; }
}
