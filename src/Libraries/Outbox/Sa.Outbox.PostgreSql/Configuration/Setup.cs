using Microsoft.Extensions.DependencyInjection;

namespace Sa.Outbox.PostgreSql.Configuration;

internal static class Setup
{
    /// <summary>
    /// Builds the configurator and records the built-in layers plus the user callback, without
    /// writing anything to DI: the caller decides whether the provider is enabled and then calls
    /// <see cref="PgOutboxConfiguration.Commit"/>.
    /// </summary>
    public static PgOutboxConfiguration CreatePgOutboxConfiguration(
        this IServiceCollection services,
        string providerKey,
        Action<IPgOutboxConfiguration>? configure = null)
    {
        var configuration = new PgOutboxConfiguration(services, providerKey);

        // Built-ins first, the user callback last (it overrides them). The serializer bridge is
        // recorded here and applied by Commit after the user's serializer, preserving the order.
        configuration.WithOutboxSettings();
        configuration.WithDataSource();

        configure?.Invoke(configuration);

        configuration.WithSerializerCompatibilityBridge();

        return configuration;
    }
}
