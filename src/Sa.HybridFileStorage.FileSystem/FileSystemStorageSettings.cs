using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage.FileSystem;

public sealed record FileSystemStorageSettings
{
    [Required]
    public string StorageType { get; init; } = "file";

    [StringLength(255)]
    public required string BasePath { get; init; }

    [StringLength(100, MinimumLength = 1)]
    public bool IsReadOnly { get; init; } = false;

    public string? ScopeName { get; init; } = null;
}
