using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage.FileSystem;

public sealed record FileSystemStorageOptions
{
    [Required]
    [StringLength(10)]
    public string StorageType { get; set; } = FileSystemStorageSettings.DefaultStorageType;
    [Required]
    [StringLength(255)]
    public string BasePath { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; } = false;
    public string? ScopeName { get; set; }
}
