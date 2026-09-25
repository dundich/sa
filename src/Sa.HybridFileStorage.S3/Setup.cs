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
    /// <returns>The same <see cref="IServiceCollection"/> instance with the service added.</returns>
    public static IServiceCollection AddSaS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.Endpoint);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.AccessKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.SecretKey);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(options.Bucket);

        // Idempotent: only the first registration wires up the bucket client + HTTP pipeline.
        // S3BucketSettings is first-wins, so guarding the whole pipeline on the same marker prevents
        // a second AddSaS3FileStorage from appending a duplicate HttpClient bound to the wrong bucket.
        var settings = new S3BucketClientSetupSettings
        {
            AccessKey = options.AccessKey,
            Bucket = options.Bucket,
            Endpoint = options.Endpoint,
            SecretKey = options.SecretKey,
            Region = options.Region ?? Defaults.DefaultRegion,
        };

        services.TryAddSingleton(settings);

        if (!services.Any(d => d.ServiceType == typeof(RegistrationMarker)))
        {
            services.AddSingleton<RegistrationMarker>();
            services.AddSaS3BucketClient(settings);
        }

        services.AddSingleton<IFileStorage, S3FileStorage>(sp => new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            options,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}

/// <summary>
/// Sentinel marker ensuring the S3 bucket client pipeline is registered exactly once.
/// </summary>
internal sealed class RegistrationMarker { }

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
