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
    /// Registration is idempotent: a second call with an identical target (endpoint, bucket,
    /// credentials, region) is a no-op, while a call targeting a different bucket or endpoint throws
    /// <see cref="InvalidOperationException"/> — the shared <see cref="IS3BucketClient"/> is first-wins,
    /// so a second storage would silently route files to the wrong bucket.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">Configuration options for the S3 storage provider.</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the services added.</returns>
    public static IServiceCollection AddSaS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.Endpoint);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.AccessKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.SecretKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.Bucket);

        var settings = new S3BucketClientSetupSettings
        {
            AccessKey = options.AccessKey,
            Bucket = options.Bucket,
            Endpoint = options.Endpoint,
            SecretKey = options.SecretKey,
            Region = options.Region ?? Defaults.DefaultRegion,
        };

        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(RegistrationMarker))
            ?.ImplementationInstance as RegistrationMarker;
        if (existing is not null)
        {
            if (IsSameTarget(existing.Settings, settings))
            {
                // Identical target: the bucket client and the storage are already registered.
                return services;
            }

            throw new InvalidOperationException(
                $"AddSaS3FileStorage has already been registered for bucket '{existing.Settings.Bucket}' " +
                $"at '{existing.Settings.Endpoint}'. A second S3 storage targeting '{settings.Bucket}' " +
                $"at '{settings.Endpoint}' is not supported: the shared IS3BucketClient is first-wins and " +
                $"the second storage would silently route files to the wrong bucket. " +
                "Register only one S3 storage per service collection.");
        }

        // Idempotent pipeline: the bucket client + HTTP pipeline is registered exactly once.
        services.AddSingleton(new RegistrationMarker(settings));
        services.TryAddSingleton(settings);
        services.AddSaS3BucketClient(settings);

        services.AddSingleton<IFileStorage, S3FileStorage>(sp => new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }

    private static bool IsSameTarget(S3BucketClientSetupSettings a, S3BucketClientSetupSettings b)
        => a.Endpoint == b.Endpoint
        && a.Bucket == b.Bucket
        && a.AccessKey == b.AccessKey
        && a.SecretKey == b.SecretKey
        && a.Region == b.Region;
}

/// <summary>
/// Sentinel marker holding the settings of the S3 bucket client pipeline registered by
/// <see cref="Setup.AddSaS3FileStorage"/>. Ensures the pipeline is registered exactly once and
/// lets a conflicting second registration fail fast.
/// </summary>
internal sealed class RegistrationMarker(S3BucketClientSetupSettings settings)
{
    /// <summary>
    /// Gets the settings of the registered S3 bucket client.
    /// </summary>
    public S3BucketClientSetupSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));
}

/// <summary>
/// Default AWS region used when <see cref="S3FileStorageOptions.Region"/> is not specified.
/// </summary>
public static class Defaults
{
    /// <summary>
    /// Gets the default region applied to S3 bucket clients.
    /// </summary>
    public const string DefaultRegion = "eu-central-1";
}
