using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage.FileSystem;

/// <summary>
/// Configuration options for the filesystem file storage provider.
/// </summary>
/// <remarks>
/// <para>
/// A single mutable type served by the standard <c>Microsoft.Extensions.Options</c> pipeline:
/// <c>Configure</c> runs first (pre-initialisation, raw values), then <c>PostConfigure</c>
/// (normalisation), then validation. The provider used to ship two near-identical types —
/// a mutable <c>FileSystemStorageOptions</c> and an immutable <c>FileSystemStorageSettings</c>
/// joined by a hand-written <c>ToSettings()</c> copy, which is exactly how a property
/// (<c>BufferSize</c>) once went missing from the registration.
/// </para>
/// <para>
/// The storage root and the file I/O itself are no longer configured here: they live on
/// <see cref="Sa.Data.TempFolder.TempFolderOptions"/> (<c>RootPath</c>, <c>MaxAge</c>, overwrite
/// policy) reached through <see cref="IFileSystemStorageBuilder.TempFolder"/>. This type keeps
/// only what shapes the file-ID / basket view of the storage.
/// </para>
/// </remarks>
public sealed class FileSystemStorageOptions
{
    /// <summary>
    /// Gets the default storage type identifier.
    /// </summary>
    public const string DefaultStorageType = "fs";

    /// <summary>
    /// Gets the default basket name.
    /// </summary>
    public const string DefaultBasket = Sa.HybridFileStorage.StorageNaming.DefaultBasket;

    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <see cref="DefaultStorageType"/> (<c>"fs"</c>).
    /// </summary>
    public string StorageType { get; set; } = DefaultStorageType;

    /// <summary>
    /// Gets or sets the basket (container) name. Must be 3–63 characters, start with a letter or underscore.
    /// Defaults to <see cref="DefaultBasket"/> (<c>"share"</c>).
    /// </summary>
    public string Basket { get; set; } = DefaultBasket;

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <see cref="Basket"/> or <see cref="StorageType"/> is invalid.</exception>
    /// <remarks>
    /// Called by <see cref="FileSystemStorageOptionsValidator"/> after the post-configuration step,
    /// so it validates normalised values (trimmed names). Explicit checks rather than
    /// <c>ValidateDataAnnotations()</c>: the latter is marked <c>RequiresUnreferencedCode</c>
    /// (IL2026) and breaks Native AOT.
    /// </remarks>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Basket))
        {
            throw new ValidationException("Basket cannot be empty.");
        }

        try
        {
            StorageNaming.ValidateBasket(Basket, nameof(Basket));
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message, ex);
        }

        if (string.IsNullOrWhiteSpace(StorageType))
        {
            throw new ValidationException("StorageType cannot be empty.");
        }

        try
        {
            StorageNaming.RequireStorageType(StorageType, nameof(StorageType));
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message, ex);
        }
    }
}
