using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

internal static class Setup
{
    public static IServiceCollection AddPgOutboxSettings(
        this IServiceCollection services,
        string providerKey,
        Action<IPgOutboxConfiguration>? configure = null)
    {
        var configuration = new PgOutboxConfiguration(services, providerKey);

        configuration.WithOutboxSettings();
        configuration.WithDataSource();

        configure?.Invoke(configuration);

        // After the user's configuration: a serializer registered under the legacy provider type
        // must stay visible to the core service the provider resolves, and with none at all the
        // core JSON fallback is used.
        configuration.WithSerializerCompatibilityBridge();

        return services;
    }
}
