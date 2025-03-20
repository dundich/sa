namespace Sa.HybridFileStorage.Domain;

public record FileMetadataInput
{
    public string TenantId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string? StorageType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}


//public class FileMetadata
//{
//    public string FileId { get; set; } = string.Empty;
//    public string TenantId { get; set; } = string.Empty;
//    public string FileName { get; set; } = string.Empty;
//    public string FileType { get; set; } = string.Empty;
//    public long FileSize { get; set; }
//    public string? StorageType { get; set; }
//    public DateTimeOffset CreatedAt { get; set; }
//}