using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Mutable configuration options for the filesystem file storage provider, used with fluent builder pattern.
/// </summary>
public sealed record FileSystemStorageOptions
{
    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <see cref="FileSystemStorageSettings.DefaultStorageType"/> (<c>"fs"</c>).
    /// </summary>
    [Required]
    [StringLength(10)]
    public string StorageType { get; set; } = FileSystemStorageSettings.DefaultStorageType;

    /// <summary>
    /// Gets or sets the base directory path where files will be stored.
    /// </summary>
    [Required]
    [StringLength(255)]
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// Gets or sets the basket (container) name. Must be 3–63 characters, start with a letter or underscore.
    /// Defaults to <see cref="FileSystemStorageSettings.DefaultBasket"/> (<c>"share"</c>).
    /// </summary>
    [Required]
    [StringLength(63, MinimumLength = 3)]
    public string Basket { get; set; } = FileSystemStorageSettings.DefaultBasket;

    /// <summary>
    /// Gets or sets the buffer size used for file I/O operations. Defaults to 256 KB.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int BufferSize { get; set; } = 256 * 1024;

    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <see cref="BasePath"/>, <see cref="Basket"/>, <see cref="StorageType"/>, or <see cref="BufferSize"/> is invalid.</exception>
    /// <remarks>
    /// Validation is delegated to <see cref="FileSystemStorageSettings.Validate"/> so that the mutable
    /// options and the immutable settings cannot drift apart. Always map through
    /// <see cref="ToSettings"/> — a hand-written copy of the properties is how
    /// <see cref="BufferSize"/> went missing from the provider registration.
    /// </remarks>
    public void Validate() => ToSettings().Validate();

    /// <summary>
    /// Creates the immutable settings this options instance describes.
    /// </summary>
    /// <returns>A <see cref="FileSystemStorageSettings"/> with every value copied.</returns>
    public FileSystemStorageSettings ToSettings() => new()
    {
        BasePath = BasePath,
        StorageType = StorageType,
        Basket = Basket,
        IsReadOnly = IsReadOnly,
        BufferSize = BufferSize,
    };
}
