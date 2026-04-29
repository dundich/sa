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
    [Required]
    [StringLength(63, MinimumLength = 3)]
    public string Basket { get; set; } = FileSystemStorageSettings.DefaultBasket;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BasePath))
        {
            throw new ValidationException("BasePath cannot be empty.");
        }

        try
        {
            // Get the full path to resolve any relative paths
            string fullPath = Path.GetFullPath(BasePath);

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
