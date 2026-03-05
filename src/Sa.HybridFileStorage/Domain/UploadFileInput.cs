namespace Sa.HybridFileStorage.Domain;

public sealed record UploadFileInput
{
    public int TenantId { get; init; }
    public string FileName { get; init; } = string.Empty;
}
