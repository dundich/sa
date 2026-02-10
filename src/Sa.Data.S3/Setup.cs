using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Sa.Data.S3;

public static class Setup
{
    public static IServiceCollection AddSaS3BucketClient(this IServiceCollection services, S3BucketClientSetupSettings settings)
    {

        services.TryAddSingleton<S3BucketSettings>(settings);

        // https://www.milanjovanovic.tech/blog/the-right-way-to-use-httpclient-in-dotnet
        services
            .AddHttpClient<IS3BucketClient, S3BucketClient>((sp, client) =>
            {
                client.Timeout = settings.TotalRequestTimeout;
                client.BaseAddress = new Uri(settings.Endpoint);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                return new SocketsHttpHandler()
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15)
                };
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = settings.TotalRequestTimeout;
            });

        return services;
    }
}
