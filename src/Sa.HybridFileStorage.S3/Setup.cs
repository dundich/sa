using Microsoft.Extensions.DependencyInjection;
using Sa.Data.S3;

namespace Sa.HybridFileStorage.S3;

public static class Setup
{
    public static IServiceCollection AddS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        var settings = new S3BucketClientSettings
        {
            AccessKey = options.AccessKey,
            Bucket = options.Bucket,
            Endpoint = options.Endpoint,
            SecretKey = options.SecretKey,
            Region = options.Region ?? "eu-central-1",
        };

        services
            .AddSaInfrastructure()
            .AddS3BucketClientAsSingleton(settings);

        return services;
    }
}
