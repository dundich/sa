using Microsoft.Extensions.DependencyInjection;
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
    public static IServiceCollection AddSaS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        var settings = new S3BucketClientSetupSettings
        {
            AccessKey = options.AccessKey,
            Bucket = options.Bucket,
            Endpoint = options.Endpoint,
            SecretKey = options.SecretKey,
            Region = options.Region ?? "eu-central-1",
        };

        services.AddSaS3BucketClient(settings);

        services.AddSingleton<IFileStorage, S3FileStorage>(sp => new S3FileStorage(
            sp.GetRequiredService<IS3BucketClient>(),
            options,
            sp.GetService<TimeProvider>()));

        return services;
    }
}
