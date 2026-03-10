using Microsoft.Extensions.DependencyInjection;
using Sa.Data.S3;
using Sa.HybridFileStorage.Domain;

namespace Sa.HybridFileStorage.S3;

public static class Setup
{
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
