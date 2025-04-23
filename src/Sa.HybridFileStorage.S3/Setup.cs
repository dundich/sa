using Microsoft.Extensions.DependencyInjection;
using Sa.Data.S3;

namespace Sa.HybridFileStorage.S3;

public static class Setup
{
    public static IServiceCollection AddS3FileStorage(this IServiceCollection services, S3FileStorageOptions options)
    {
        var uri = new Uri(options.Endpoint);
        var settings = S3BucketClientSettings.Create(uri, options.AccessKey, options.SecretKey, options.Bucket, options.Region);

        services
            .AddSaInfrastructure()
            .AddS3BucketClient(settings);

        return services;
    }
}
