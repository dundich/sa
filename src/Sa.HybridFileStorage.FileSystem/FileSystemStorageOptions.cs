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
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <paramref name="BasePath"/>, <paramref name="Basket"/>, or <paramref name="StorageType"/> is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BasePath))
        {
            throw new ValidationException("BasePath cannot be empty.");
        }

        try
        {
            // Get the full path to resolve any relative paths
            var _ = Path.GetFullPath(BasePath);

            // Check if the path contains any invalid characters
            if (BasePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ValidationException($"BasePath contains invalid characters: {BasePath}");
            }
        }
        catch (Exception ex)
        {
            throw new ValidationException($"Invalid BasePath format: {BasePath}. {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(Basket))
        {
            throw new ValidationException("Basket cannot be empty.");
        }


        if (Basket.Length > 63 || Basket.Length < 3)
        {
            throw new ValidationException($"Basket exceeds maximum length of 63 characters.");
        }

        if (!char.IsLetter(Basket[0]) && Basket[0] != '_')
        {
            throw new ValidationException("Basket must start with a letter or underscore.");
        }


        if (string.IsNullOrWhiteSpace(Basket))
        {
            throw new ValidationException("Basket cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(StorageType))
        {
            throw new ValidationException("StorageType cannot be empty.");
        }

        if (StorageType.Length > 10)
        {
            throw new ValidationException($"StorageType exceeds maximum length of 63 characters.");
        }
    }
}
