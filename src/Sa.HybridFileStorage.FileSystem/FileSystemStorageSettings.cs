namespace Sa.HybridFileStorage.FileSystem;

public sealed record FileSystemStorageSettings
{
    public string StorageType { get; init; } = DefaultStorageType;

    public string Basket { get; init; } = DefaultBasket;

    public required string BasePath { get; init; }

    public bool IsReadOnly { get; init; } = false;

    public int BufferSize { get; init; } = 256 * 1024;


    public const string DefaultStorageType = "fs";
    public const string DefaultBasket = "share";
}
