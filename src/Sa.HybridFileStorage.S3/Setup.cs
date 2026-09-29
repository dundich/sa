using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.S3;

/// <summary>
/// Provides extension methods for registering the S3 file storage provider with the .NET Generic Host.
/// </summary>
public static class Setup
{
    /// <summary>
    /// Registers the S3 file storage provider with the specified service collection.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the S3 storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or <paramref name="options"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when an option is missing or malformed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a conflicting S3 storage was already registered.</exception>
    /// <remarks>
    /// Registration is idempotent. A second call is a no-op only when it would produce the very same
    /// storage: the shared <see cref="IS3BucketClient"/> is first-wins, so a differing target cannot
    /// be honoured, and a differing basket or storage type would be silently ignored while the caller
    /// observed the first registration's values. Anything else throws.
    /// <para>
    /// Credentials are deliberately excluded from the comparison: rotating them must not fail a
    /// startup that would otherwise be correct, and the client cannot be rebuilt anyway.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSaS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Fail fast at registration: a blank endpoint used to surface as a bare UriFormatException
        // from inside the bucket client setup, with no indication of which option was at fault.
        options.Validate();

        var settings = options.ToBucketClientSettings();

        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(S3FileStorageRegistration))
            ?.ImplementationInstance as S3FileStorageRegistration;
        if (existing is not null)
        {
            if (IsSameTarget(existing.Client, settings))
            {
                // Same bucket and endpoint: the client is already wired up. The storage options must
                // match too, or the caller would get the first registration's basket/storage type.
                if (existing.Options.HasSameStorageIdentity(options))
                {
                    return services;
                }

                throw new InvalidOperationException(
                    $"AddSaS3FileStorage has already been registered for bucket '{existing.Client.Bucket}' " +
                    $"at '{existing.Client.Endpoint}' with a different basket or storage type " +
                    $"('{existing.Options.Basket}'/'{existing.Options.StorageType}' versus " +
                    $"'{options.Basket}'/'{options.StorageType}'). The second call would be ignored, " +
                    "so the storage would silently use the first registration's names. " +
                    "Register only one S3 storage per service collection.");
            }

            throw new InvalidOperationException(
                $"AddSaS3FileStorage has already been registered for bucket '{existing.Client.Bucket}' " +
                $"at '{existing.Client.Endpoint}'. A second S3 storage targeting '{settings.Bucket}' " +
                $"at '{settings.Endpoint}' is not supported: the shared IS3BucketClient is first-wins and " +
                "the second storage would silently route files to the wrong bucket. " +
                "Register only one S3 storage per service collection.");
        }

        // Idempotent pipeline: the bucket client + HTTP pipeline is registered exactly once.
        services.AddSingleton(new S3FileStorageRegistration(options, settings));
        services.AddSaS3BucketClient(settings);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IFileStorage>(sp => new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            options,
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }

    private static bool IsSameTarget(S3BucketClientSetupSettings a, S3BucketClientSetupSettings b)
        => a.Endpoint == b.Endpoint
        && a.Bucket == b.Bucket
        && a.Region == b.Region;
}

/// <summary>
/// Sentinel marker holding the options and bucket client settings of the S3 storage registered by
/// <see cref="Setup.AddSaS3FileStorage"/>. Ensures the pipeline is registered exactly once and lets
/// a conflicting second registration fail fast.
/// </summary>
internal sealed class S3FileStorageRegistration(
    S3FileStorageOptions options,
    S3BucketClientSetupSettings client)
{
    /// <summary>
    /// Gets the storage options the provider was registered with.
    /// </summary>
    public S3FileStorageOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Gets the settings of the registered S3 bucket client.
    /// </summary>
    public S3BucketClientSetupSettings Client { get; } = client ?? throw new ArgumentNullException(nameof(client));
}
