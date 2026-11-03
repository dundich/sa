using System.Diagnostics.CodeAnalysis;

namespace Sa.HybridFileStorage;

/// <summary>
/// Single source of truth for the naming rules shared by every <see cref="Domain.IFileStorage"/>
/// provider: the storage type (the file ID scheme, e.g. <c>fs</c>) and the basket (container) name.
/// </summary>
/// <remarks>
/// These rules are enforced at registration time so that a misconfigured provider fails fast at
/// startup instead of producing unusable file IDs or unresolvable SQL identifiers at runtime.
/// </remarks>
public static class StorageNaming
{
    /// <summary>
    /// The default basket (container) name shared across file storage providers.
    /// </summary>
    public const string DefaultBasket = "share";

    /// <summary>
    /// The minimum length of a basket name.
    /// </summary>
    public const int BasketMinLength = 3;

    /// <summary>
    /// The maximum length of a basket name.
    /// </summary>
    public const int BasketMaxLength = 63;

    /// <summary>
    /// The maximum length of a storage type identifier.
    /// </summary>
    public const int StorageTypeMaxLength = 10;

    /// <summary>
    /// The maximum length of a SQL identifier (table or schema name).
    /// </summary>
    public const int IdentifierMaxLength = 63;

    /// <summary>
    /// Validates a basket (container) name: 3–63 characters, starting with a letter or an underscore,
    /// and free of path separators.
    /// </summary>
    /// <param name="basket">The basket name to validate.</param>
    /// <param name="paramName">The name of the caller's parameter, used in the exception message.</param>
    /// <exception cref="ArgumentException">Thrown when the basket name is missing or malformed.</exception>
    /// <remarks>
    /// The basket is used verbatim as a directory name by the filesystem and in-memory providers
    /// and as a path segment of a file ID, so a separator in it would either escape the base
    /// directory or split the file ID into an extra segment. Dashes are allowed.
    /// </remarks>
    public static void ValidateBasket([NotNull] string? basket, string paramName)
    {
        if (string.IsNullOrWhiteSpace(basket))
        {
            throw new ArgumentException("Basket cannot be empty.", paramName);
        }

        if (basket.Length > BasketMaxLength || basket.Length < BasketMinLength)
        {
            throw new ArgumentException(
                $"Basket must be between {BasketMinLength} and {BasketMaxLength} characters long, but was {basket.Length}.",
                paramName);
        }

        if (!char.IsLetter(basket[0]) && basket[0] != '_')
        {
            throw new ArgumentException("Basket must start with a letter or an underscore.", paramName);
        }

        if (basket.Contains('/') || basket.Contains('\\'))
        {
            throw new ArgumentException("Basket must not contain a path separator.", paramName);
        }
    }

    /// <summary>
    /// Validates a storage type identifier and returns it unchanged.
    /// The value becomes the scheme of a file ID (<c>fs://</c>), which
    /// <see cref="FileIdParser"/> locates with <c>IndexOf("://")</c> — so a colon or a slash in
    /// the value would make the produced file ID unparsable. Dashes and dots are allowed.
    /// </summary>
    /// <param name="storageType">The storage type identifier to validate.</param>
    /// <param name="paramName">The name of the caller's parameter, used in the exception message.</param>
    /// <returns>The validated storage type identifier.</returns>
    /// <exception cref="ArgumentException">Thrown when the storage type is missing or malformed.</exception>
    public static string RequireStorageType([NotNull] string? storageType, string paramName)
    {
        if (string.IsNullOrWhiteSpace(storageType))
        {
            throw new ArgumentException("StorageType cannot be empty.", paramName);
        }

        if (storageType.Length > StorageTypeMaxLength)
        {
            throw new ArgumentException(
                $"StorageType exceeds the maximum length of {StorageTypeMaxLength} characters, but was {storageType.Length}.",
                paramName);
        }

        if (storageType.Contains(':') || storageType.Contains('/') || storageType.Contains('\\'))
        {
            throw new ArgumentException(
                "StorageType must not contain ':', '/' or '\\' — it becomes the scheme of a file ID.",
                paramName);
        }

        return storageType;
    }

    /// <summary>
    /// Validates a SQL identifier (a table or schema name) and returns it unchanged.
    /// The value is interpolated into DDL, so anything beyond a bare identifier is rejected outright
    /// rather than silently rewritten — a rewritten name produces a table that queries cannot find.
    /// </summary>
    /// <param name="identifier">The identifier to validate.</param>
    /// <param name="paramName">The name of the caller's parameter, used in the exception message.</param>
    /// <param name="maxLength">The maximum allowed length. Defaults to <see cref="IdentifierMaxLength"/>.</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="ArgumentException">Thrown when the identifier is missing or malformed.</exception>
    public static string RequireIdentifier(
        [NotNull] string? identifier,
        string paramName,
        int maxLength = IdentifierMaxLength)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", paramName);
        }

        if (identifier.Length > maxLength)
        {
            throw new ArgumentException(
                $"Identifier exceeds the maximum length of {maxLength} characters, but was {identifier.Length}.",
                paramName);
        }

        // A bare identifier: a letter or underscore, then letters/digits/underscores. Quoting
        // ("files") is rejected on purpose: an embedded quote reaches DDL and breaks the statement,
        // and it is never what the caller meant.
        if (!char.IsLetter(identifier[0]) && identifier[0] != '_')
        {
            throw new ArgumentException(
                "Identifier must start with a letter or an underscore.", paramName);
        }

        if (!IsWord(identifier))
        {
            throw new ArgumentException(
                "Identifier may only contain letters, digits and underscores.", paramName);
        }

        return identifier;
    }

    /// <summary>
    /// Gets a value indicating whether every character of <paramref name="value"/> is a letter,
    /// a digit or an underscore.
    /// </summary>
    private static bool IsWord(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
