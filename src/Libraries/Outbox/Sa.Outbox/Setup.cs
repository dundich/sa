using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;

namespace Sa.Outbox;

public static class Setup
{
    /// <summary>
    /// Registers the Sa.Outbox infrastructure in the service collection and optionally configures it via a builder action.
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="build">An optional action to configure the outbox through <see cref="IOutboxBuilder"/>.</param>
    /// <param name="scopeKey">
    /// The dependency-injection scope of the outbox — the key under which its internal schedule
    /// (delivery, migration and cleanup jobs) is registered. Defaults to
    /// <see cref="OutboxScope.DefaultKey"/>. The Outbox never uses the application's default
    /// schedule; when a provider package such as <c>AddSaOutboxUsingPostgreSql</c> is used, pass
    /// the same value to both so the outbox partitional module and its schedule agree.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutbox(
        this IServiceCollection services,
        Action<IOutboxBuilder>? build = null,
        string scopeKey = OutboxScope.DefaultKey)
    {
        OutboxScope.EnsureValid(scopeKey);

        // Registered here as well as by AddOutboxProvider (TryAddEnumerable deduplicates by
        // implementation type): the scope-key check must also run when consumer groups were
        // registered but no provider package was called at all. Registering it before the builder
        // puts the validator ahead of the schedule host in the hosted-service list, so it runs
        // before any delivery job can tick even when the provider is registered afterwards.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxProviderValidationHostedService>());

        if (build != null)
        {
            // A repeated AddSaOutbox for the same scope replaces the publish-settings callbacks the
            // previous registration recorded instead of stacking a second copy. The removal lives
            // here, not in AddMessagePublisher, so a provider's internal AddSaOutbox(null) (the
            // split-form AddSaOutbox(...).AddSaOutboxUsingPostgreSql(...) registration) cannot wipe
            // settings the application already configured.
            services.RemoveAllKeyed<Func<IServiceProvider, OutboxPublishSettings, OutboxPublishSettings>>(
                scopeKey);
        }

        OutboxBuilder builder = OutboxBuilder.Create(services, scopeKey);
        build?.Invoke(builder);

        services.TryAddSingleton<IDeliveryBackoffStrategy, ExponentialDeliveryBackoffStrategy>();

        // Keyed publisher lookup for multi-provider registrations.
        services.TryAddSingleton<IOutboxPublisherResolver, OutboxPublisherResolver>();

        // Aggregated provider health (each provider may register its own keyed check).
        services.TryAddSingleton<IOutboxHealth, OutboxHealth>();

        return services;
    }
}
