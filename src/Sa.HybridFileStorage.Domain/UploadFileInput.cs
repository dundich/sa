namespace Sa.HybridFileStorage.Domain;

public record UploadFileInput
{
    public int TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
}
