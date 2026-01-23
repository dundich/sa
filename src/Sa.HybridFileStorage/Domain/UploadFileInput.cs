namespace Sa.HybridFileStorage.Domain;

public sealed record UploadFileInput
{
    public int TenantId { get; set; }
    public string FileName { get; set; } = string.Empty;
}
