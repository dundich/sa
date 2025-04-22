using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Data.S3;

public static class Setup
{
    public static IServiceCollection AddS3BucketClient(this IServiceCollection services, S3BucketClientSettings settings)
    {
        services.TryAddSingleton<S3BucketClientSettings>(settings);
        services.TryAddSingleton<IS3BucketClient, S3BucketClient>();
        return services;
    }
}
