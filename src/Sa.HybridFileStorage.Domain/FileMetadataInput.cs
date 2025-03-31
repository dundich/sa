namespace Sa.HybridFileStorage.Domain;

public record FileMetadataInput
{
    public int TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
