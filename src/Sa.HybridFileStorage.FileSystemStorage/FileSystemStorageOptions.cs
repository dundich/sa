namespace Sa.HybridFileStorage.FileSystemStorage;

public class FileSystemStorageOptions
{
    public string StorageType { get; set; } = "file";
    public string BasePath { get; set; } = string.Empty;
    public bool? IsReadOnly { get; set; }
}
