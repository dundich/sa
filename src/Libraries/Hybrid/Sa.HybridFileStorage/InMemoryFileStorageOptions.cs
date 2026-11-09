using System.ComponentModel.DataAnnotations;

namespace Sa.HybridFileStorage;

/// <summary>
/// Configuration options for the in-memory file storage provider.
/// </summary>
/// <param name="Basket">The default basket (container) name. Defaults to <c>"share"</c>.</param>
/// <param name="IsReadOnly"><c>true</c> if the storage should reject write operations; otherwise, <c>false</c>.</param>
/// <param name="MaxSizeBytes">Maximum total size in bytes for all stored files. Default is 1 GB (<c>1_073_741_824</c>). Set to zero or negative to disable the limit.</param>
public sealed record InMemoryFileStorageOptions(
    string Basket = "share",
    bool IsReadOnly = false,
    long MaxSizeBytes = 1_073_741_824)
{
    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> when the
    /// basket name is malformed.
    /// </summary>
    /// <remarks>
    /// An <b>empty</b> basket is a supported configuration here (it produces a two-segment file ID,
    /// <c>scheme://tenant/file</c>) and is deliberately not rejected — several call sites rely on
    /// it. A <b>null</b> basket is not the same thing: it is a broken configuration that would fail
    /// later inside <see cref="System.IO.Path.Combine(string, string, string)"/>, so it is rejected
    /// here. A non-empty basket must satisfy the same rules as every other provider, so a value
    /// with a path separator or an out-of-range name fails instead of silently forming a file ID no
    /// other provider could parse. <see cref="MaxSizeBytes"/> is not checked: zero and negative
    /// values are the documented way to disable the limit.
    /// </remarks>
    /// <exception cref="ValidationException">Thrown when <see cref="Basket"/> is null or malformed.</exception>
    public void Validate()
    {
        if (Basket is null)
        {
            throw new ValidationException(
                "Basket cannot be null. Use an empty string for a basket-less storage.");
        }

        if (Basket.Length == 0)
        {
            return;
        }

        try
        {
            StorageNaming.ValidateBasket(Basket, nameof(Basket));
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message, ex);
        }
    }
}
