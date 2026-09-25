using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <see cref="BasePath"/>, <see cref="Basket"/>, or <see cref="StorageType"/> is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BasePath))
        {
            throw new ValidationException("BasePath cannot be empty.");
        }

        try
        {
            // Resolve any relative path to detect malformed input early.
            Path.GetFullPath(BasePath);

            if (BasePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                throw new ValidationException($"BasePath contains invalid characters: {BasePath}");
            }
        }
        catch (Exception ex) when (ex is not ValidationException)
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

        if (string.IsNullOrWhiteSpace(StorageType))
        {
            throw new ValidationException("StorageType cannot be empty.");
        }

        if (StorageType.Length > 10)
        {
            throw new ValidationException($"StorageType exceeds maximum length of 10 characters.");
        }
    }
}
