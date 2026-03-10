using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage.FileSystem;

public sealed record FileSystemStorageSettings
{
    [Required]
    public string StorageType { get; init; } = DefaultStorageType;

    [StringLength(255)]
    public required string BasePath { get; init; }

    [StringLength(100, MinimumLength = 1)]
    public bool IsReadOnly { get; init; } = false;

    public string ScopeName { get; init; } = string.Empty;

    public int BufferSize { get; init; } = 256 * 1024;


    public const string DefaultStorageType = "fs";
}
