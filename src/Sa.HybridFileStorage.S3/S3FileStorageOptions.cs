using Sa.Data.S3;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Configuration options for the S3 (MinIO-compatible) file storage provider.
/// </summary>
public sealed record S3FileStorageOptions
{
    /// <summary>
    /// Gets the default basket (container) name shared across file storage providers.
    /// </summary>
    public const string DefaultBasket = StorageNaming.DefaultBasket;

    /// <summary>
    /// Gets or sets the storage type identifier. Defaults to <c>"s3"</c>.
    /// </summary>
    public string StorageType { get; init; } = "s3";

    /// <summary>
    /// Gets or sets the basket (container) name. Defaults to <see cref="DefaultBasket"/> (<c>"share"</c>).
    /// </summary>
    public string Basket { get; init; } = DefaultBasket;

    /// <summary>
    /// Gets or sets the S3-compatible endpoint URL (e.g., <c>http://localhost:9000</c>).
    /// Must be an absolute HTTP or HTTPS URL; a trailing slash is trimmed.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Gets or sets the access key for authenticating with the S3 service.
    /// </summary>
    public required string AccessKey { get; init; }

    /// <summary>
    /// Gets or sets the secret key for authenticating with the S3 service.
    /// </summary>
    public required string SecretKey { get; init; }

    /// <summary>
    /// Gets or sets the name of the S3 bucket to use for file storage.
    /// </summary>
    public required string Bucket { get; init; }

    /// <summary>
    /// Gets or sets the AWS region. Defaults to <see cref="S3Defaults.DefaultRegion"/> (<c>"eu-central-1"</c>).
    /// </summary>
    public string Region { get; init; } = S3Defaults.DefaultRegion;

    /// <summary>
    /// Gets or sets a value indicating whether this storage is read-only. Defaults to <c>false</c>.
    /// </summary>
    public bool IsReadOnly { get; init; } = false;

    /// <summary>
    /// Gets or sets transport-level settings for the shared bucket client
    /// (request timeout, connection pool lifetime, handler lifetime).
    /// Defaults to <c>null</c>, which keeps <see cref="S3BucketClientSetupSettings"/> defaults.
    /// </summary>
    public S3BucketClientSetupSettings? ClientSettings { get; init; }

    /// <summary>
    /// Validates the current configuration and throws an <see cref="ArgumentException"/> describing
    /// the first offending property.
    /// </summary>
    /// <param name="paramName">
    /// The name of the caller's parameter, used as the base of the exception's parameter name.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when any property is invalid.</exception>
    /// <remarks>
    /// Called from the registration extension. The endpoint check matters in particular: the value
    /// reaches <c>new Uri(...)</c> inside the S3 client setup, which would otherwise surface as a
    /// bare <see cref="UriFormatException"/> with no indication of which option was at fault.
    /// </remarks>
    public void Validate(string paramName = "options")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint, $"{paramName}.Endpoint");
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessKey, $"{paramName}.AccessKey");
        ArgumentException.ThrowIfNullOrWhiteSpace(SecretKey, $"{paramName}.SecretKey");
        ArgumentException.ThrowIfNullOrWhiteSpace(Bucket, $"{paramName}.Bucket");
        ArgumentException.ThrowIfNullOrWhiteSpace(Region, $"{paramName}.Region");

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"Endpoint must be an absolute http or https URL, but was '{Endpoint}'.", $"{paramName}.Endpoint");
        }

        StorageNaming.RequireStorageType(StorageType, $"{paramName}.StorageType");
        StorageNaming.ValidateBasket(Basket, $"{paramName}.Basket");
    }

    /// <summary>
    /// Determines whether these options describe the same storage as <paramref name="other"/>:
    /// the same basket, storage type and read-only flag.
    /// </summary>
    /// <param name="other">The options to compare with.</param>
    /// <returns><c>true</c> when a second registration would be indistinguishable to callers.</returns>
    /// <remarks>
    /// This is deliberately narrower than record equality. Credentials and
    /// <see cref="ClientSettings"/> are applied once, when the shared bucket client is built, and
    /// cannot be changed by a later registration — so differing values there are a no-op rather
    /// than a conflict, and must not be reported as one. A whole-record comparison conflated the
    /// two and rejected a mere credential rotation.
    /// </remarks>
    public bool HasSameStorageIdentity(S3FileStorageOptions? other)
        => other is not null
        && string.Equals(Basket, other.Basket, StringComparison.Ordinal)
        && string.Equals(StorageType, other.StorageType, StringComparison.Ordinal)
        && IsReadOnly == other.IsReadOnly;

    /// <summary>
    /// Builds the bucket client settings for this configuration, merging in
    /// <see cref="ClientSettings"/> and normalising the endpoint.
    /// </summary>
    /// <returns>The settings to hand to the S3 bucket client registration.</returns>
    public S3BucketClientSetupSettings ToBucketClientSettings()
    {
        // Start from the real defaults rather than restating them, so a change to
        // S3BucketClientSetupSettings cannot silently drift away from this projection.
        var settings = new S3BucketClientSetupSettings
        {
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Bucket = Bucket,
            Endpoint = Endpoint.TrimEnd('/'),
            Region = Region,
        };

        if (ClientSettings is not null)
        {
            settings.TotalRequestTimeout = ClientSettings.TotalRequestTimeout;
            settings.ConnectionPoolLifetime = ClientSettings.ConnectionPoolLifetime;
            settings.HandlerLifetime = ClientSettings.HandlerLifetime;
        }

        return settings;
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
