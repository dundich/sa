using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.Redis.Configuration;

internal static class Setup
{
    /// <summary>
    /// Builds the configurator and records the built-in layers plus the user callback, without
    /// writing anything to DI: the caller decides whether the provider is enabled and then calls
    /// <see cref="RedisOutboxConfiguration.Commit"/>.
    /// </summary>
    public static RedisOutboxConfiguration CreateRedisOutboxConfiguration(
        this IServiceCollection services,
        string providerKey,
        Action<IRedisOutboxConfiguration>? configure = null)
    {
        var configuration = new RedisOutboxConfiguration(services, providerKey);

        configuration.WithOutboxSettings();
        configuration.WithConnection();

        configure?.Invoke(configuration);

        // After the user's configuration: a serializer registered under the legacy provider type
        // must stay visible to the core service the provider resolves, and with none at all the
        // core JSON fallback is used.
        configuration.WithSerializerCompatibilityBridge();

        return configuration;
    }
}
