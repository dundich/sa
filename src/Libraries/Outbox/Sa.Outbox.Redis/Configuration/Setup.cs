using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Redis.Configuration;

internal static class Setup
{
    public static IServiceCollection AddRedisOutboxSettings(
        this IServiceCollection services,
        Action<IRedisOutboxConfiguration>? configure = null,
        string providerKey = OutboxScope.DefaultKey)
    {
        var configuration = new RedisOutboxConfiguration(services, providerKey);

        configuration.WithOutboxSettings();
        configuration.WithConnection();

        configure?.Invoke(configuration);

        // After the user's configuration: a serializer registered under the legacy provider type
        // must stay visible to the core service the provider resolves, and with none at all the
        // core JSON fallback is used.
        configuration.WithSerializerCompatibilityBridge();

        return services;
    }
}
