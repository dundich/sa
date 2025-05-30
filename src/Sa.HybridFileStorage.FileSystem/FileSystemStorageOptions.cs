namespace Sa.HybridFileStorage.FileSystem;

public class FileSystemStorageOptions
{
    public string StorageType { get; set; } = "file";
    public required string BasePath { get; set; }
    public bool? IsReadOnly { get; set; }
}
