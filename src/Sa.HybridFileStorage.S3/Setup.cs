using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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


        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        services.AddSaS3BucketClient(settings);


        services.TryAddSingleton(options);
        services.AddSingleton<IFileStorage, S3FileStorage>();

        return services;
    }
}
