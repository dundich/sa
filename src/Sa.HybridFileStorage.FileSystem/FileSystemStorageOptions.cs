namespace Sa.HybridFileStorage.FileSystem;

public sealed class FileSystemStorageOptions
{
    public string StorageType { get; init; } = "file";
    public required string BasePath { get; init; }
    public bool? IsReadOnly { get; init; }
}
