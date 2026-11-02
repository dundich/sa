using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Redis.Configuration;

internal static class Setup
{
    public static IServiceCollection AddRedisOutboxSettings(
        this IServiceCollection services,
        Action<IRedisOutboxConfiguration>? configure = null)
    {
        var configuration = new RedisOutboxConfiguration(services);

        configuration.WithDefaultSerializer();
        configuration.WithOutboxSettings();
        configuration.WithConnection();

        configure?.Invoke(configuration);

        return services;
    }
}
