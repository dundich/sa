namespace Sa.HybridFileStorage.FileSystem;

public sealed record FileSystemStorageOptions
{
    public string StorageType { get; init; } = "file";
    public required string BasePath { get; init; }
    public bool IsReadOnly { get; init; } = false;
    public string? ScopeName { get; init; } = null;
}
