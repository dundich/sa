namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Immutable settings for the filesystem file storage provider.
/// </summary>
public sealed record FileSystemStorageSettings
{
    /// <summary>
    /// Gets the storage type identifier. Defaults to <see cref="DefaultStorageType"/> (<c>"fs"</c>).
    /// </summary>
    public string StorageType { get; init; } = DefaultStorageType;

    /// <summary>
    /// Gets the basket (container) name. Defaults to <see cref="DefaultBasket"/> (<c>"share"</c>).
    /// </summary>
    public string Basket { get; init; } = DefaultBasket;

    /// <summary>
    /// Gets the base directory path where files will be stored.
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>
    /// Gets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; init; } = false;

    /// <summary>
    /// Gets the buffer size used for file I/O operations. Defaults to 256 KB.
    /// </summary>
    public int BufferSize { get; init; } = 256 * 1024;

    /// <summary>
    /// Gets the default storage type identifier.
    /// </summary>
    public const string DefaultStorageType = "fs";

    /// <summary>
    /// Gets the default basket name.
    /// </summary>
    public const string DefaultBasket = "share";
}
