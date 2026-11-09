using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Publication;

internal static class Setup
{
    public static IServiceCollection AddMessagePublisher(
        this IServiceCollection services,
        Func<IServiceProvider, OutboxPublishSettings, OutboxPublishSettings>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        services.AddMessagesMetadata();

        if (configure != null)
        {
            // Keyed by the outbox scope: publish settings are per provider. The settings factory
            // below is registered on the first AddMessagePublisher call, so later
            // WithPublishSettings calls still contribute through this list.
            services.AddKeyedSingleton<Func<IServiceProvider, OutboxPublishSettings, OutboxPublishSettings>>(
                scopeKey,
                configure);
        }
        // No removal here: AddMessagePublisher is also called by a provider's internal
        // AddSaOutbox(null), which must not discard callbacks recorded by an earlier user
        // registration. The "repeated AddSaOutbox replaces callbacks" rule is enforced by
        // Sa.Outbox.Setup.AddSaOutbox, which knows whether a user builder was supplied.

        services.TryAddKeyedSingleton<OutboxPublishSettings>(scopeKey, (sp, _) =>
        {
            OutboxPublishSettings settings = new();

            foreach (var build in sp.GetKeyedServices<
                Func<IServiceProvider, OutboxPublishSettings, OutboxPublishSettings>>(scopeKey))
            {
                // The settings are immutable: each delegate returns the next snapshot.
                settings = build(sp, settings);
            }

            return settings;
        });

        // The publisher is keyed by the provider key and resolves that provider's keyed writer,
        // publish settings and registry. The unkeyed fallbacks cover the migration window (a
        // provider that still registers unkeyed) and simple test containers.
        services.TryAddKeyedSingleton<IOutboxMessagePublisher>(scopeKey, (sp, _) =>
            new OutboxMessagePublisher(
                sp.GetRequiredService<TimeProvider>(),
                OutboxProviderResolution.Required<IOutboxBulkWriter>(sp, scopeKey),
                sp.GetRequiredKeyedService<OutboxPublishSettings>(scopeKey),
                sp.GetRequiredService<IOutboxMessageMetadataProvider>(),
                OutboxProviderResolution.Optional<IOutboxProviderRegistry>(sp, scopeKey)));

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
