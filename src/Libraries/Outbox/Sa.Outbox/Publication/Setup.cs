using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Publication;

internal static class Setup
{
    public static IServiceCollection AddMessagePublisher(
        this IServiceCollection services,
        Action<IServiceProvider, OutboxPublishSettings>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        services.AddMessagesMetadata();

        if (configure != null)
        {
            services.AddSingleton(configure);
        }

        services.TryAddKeyedSingleton<OutboxPublishSettings>(scopeKey, (sp, _) =>
        {
            OutboxPublishSettings settings = new();

            foreach (var build in sp.GetServices<Action<IServiceProvider, OutboxPublishSettings>>())
            {
                build(sp, settings);
            }

            return settings;
        });

        // The publisher is keyed by the provider key and resolves that provider's keyed writer,
        // publish settings and registry. The unkeyed fallbacks cover the migration window (a
        // provider that still registers unkeyed) and simple test containers.
        services.TryAddKeyedSingleton<IOutboxMessagePublisher>(scopeKey, (sp, _) =>
            new OutboxMessagePublisher(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetKeyedService<IOutboxBulkWriter>(scopeKey)
                    ?? sp.GetRequiredService<IOutboxBulkWriter>(),
                sp.GetRequiredKeyedService<OutboxPublishSettings>(scopeKey),
                sp.GetRequiredService<IOutboxMessageMetadataProvider>(),
                sp.GetKeyedService<IOutboxProviderRegistry>(scopeKey)
                    ?? sp.GetService<IOutboxProviderRegistry>()));

        if (string.Equals(scopeKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            // Backward compatibility: existing code injects the publisher (and its settings)
            // without a key. Both point at the default provider's keyed instance.
            services.TryAddSingleton<IOutboxMessagePublisher>(
                sp => sp.GetRequiredKeyedService<IOutboxMessagePublisher>(OutboxScope.DefaultKey));
            services.TryAddSingleton<OutboxPublishSettings>(
                sp => sp.GetRequiredKeyedService<OutboxPublishSettings>(OutboxScope.DefaultKey));
        }

        return services;
    }
}
