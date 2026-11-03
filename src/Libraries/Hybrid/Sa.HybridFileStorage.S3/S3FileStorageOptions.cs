using System.ComponentModel.DataAnnotations;
using Sa.Data.S3;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Configuration options for the S3 (MinIO-compatible) file storage provider.
/// </summary>
/// <remarks>
/// A single mutable type served by the standard <c>Microsoft.Extensions.Options</c> pipeline:
/// <c>Configure</c> runs first (pre-initialisation, raw values), then <c>PostConfigure</c>
/// (normalisation), then validation. The connection and transport settings are inherited from
/// <see cref="S3BucketClientSetupOptions"/> — the very type the bucket client is built from — so
/// there is no second copy and no hand-written projection that can silently drop a property, and
/// <see cref="UseHttp2"/> is no longer ignored by the registration.
/// </remarks>
public sealed class S3FileStorageOptions : S3BucketClientSetupOptions
{
    /// <summary>
    /// Gets the default basket (container) name shared across file storage providers.
    /// </summary>
    public const string DefaultBasket = StorageNaming.DefaultBasket;

    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <c>"s3"</c>.
    /// </summary>
    public string StorageType { get; set; } = "s3";

    /// <summary>
    /// Gets or sets the basket (container) name. Defaults to <see cref="DefaultBasket"/> (<c>"share"</c>).
    /// </summary>
    public string Basket { get; set; } = DefaultBasket;

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; set; } = false;

    /// <summary>
    /// Initialises the options with the default region.
    /// </summary>
    /// <remarks>
    /// The base class defaults to <c>"us-east-1"</c>, which is right for the standalone bucket
    /// client but wrong for this provider — it has always defaulted to
    /// <see cref="S3Defaults.DefaultRegion"/>. Setting it here rather than redeclaring the property
    /// keeps a single <c>Region</c> on the instance.
    /// </remarks>
    public S3FileStorageOptions()
    {
        Region = S3Defaults.DefaultRegion;
    }

    /// <summary>
    /// Brings every value to its canonical form. Called by the pipeline before validation, so the
    /// checks in <see cref="Validate"/> always see normalised values.
    /// </summary>
    public override void Normalize()
    {
        base.Normalize();

        StorageType = StorageType.Trim();
        Basket = Basket.Trim();
    }

    /// <summary>
    /// Validates the current configuration and throws a <see cref="ValidationException"/> if any property is invalid.
    /// </summary>
    /// <exception cref="ValidationException">Thrown when <see cref="S3BucketClientSetupOptions.Endpoint"/>, the credentials, <see cref="Basket"/>, <see cref="StorageType"/> or a transport setting is invalid.</exception>
    /// <remarks>
    /// Called by <see cref="S3FileStorageOptionsValidator"/> after the post-configuration step, so
    /// it validates normalised values (a trimmed endpoint without a trailing slash, trimmed names).
    /// Explicit checks rather than <c>ValidateDataAnnotations()</c>: the latter is marked
    /// <c>RequiresUnreferencedCode</c> (IL2026) and breaks Native AOT.
    /// </remarks>
    public override void Validate()
    {
        base.Validate();

        try
        {
            StorageNaming.RequireStorageType(StorageType, nameof(StorageType));
            StorageNaming.ValidateBasket(Basket, nameof(Basket));
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message, ex);
        }
    }
}

/// <summary>
/// Default AWS region used when <see cref="S3FileStorageOptions.Region"/> is not specified.
/// </summary>
public static class S3Defaults
{
    /// <summary>
    /// Gets the default region applied to S3 bucket clients.
    /// </summary>
    public const string DefaultRegion = "eu-central-1";
}
