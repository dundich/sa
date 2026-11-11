using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.InMemory;

/// <summary>
/// Registration of the in-memory Outbox provider.
/// </summary>
/// <remarks>
/// <para>
/// The provider is a <em>reference implementation</em> of the plug point and a development/test
/// substitute for a real store: messages live in process memory, so a restart loses everything and
/// there is no cross-process delivery. It is deliberately not a production provider.
/// </para>
/// <para>
/// It implements the three contracts the core consumes — bulk writer, delivery manager, tenant
/// detector — and nothing else: no health probe, no warm-up, no migrations, no retention. That is the
/// whole point: the file layout is the documentation for embedding a provider.
/// </para>
/// </remarks>
public static class Setup
{
    /// <summary>
    /// Registers the in-memory Outbox: a process-local store with no external dependency.
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure <see cref="InMemoryOutboxSettings"/>.</param>
    /// <param name="configureOutbox">
    /// An optional action to configure the provider-agnostic Outbox core (<see cref="IOutboxBuilder"/>):
    /// tenants, deliveries, metadata, serializer.
    /// </param>
    /// <param name="scopeKey">
    /// The provider key and dependency-injection scope of the outbox. Defaults to
    /// <see cref="OutboxScope.DefaultKey"/>; use one key per provider when the container holds several.
    /// </param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty,
    /// whitespace, or <see cref="Sa.Schedule.Setup.DefaultName"/>.</exception>
    public static IServiceCollection AddSaOutboxUsingInMemory(
        this IServiceCollection services,
        Action<InMemoryOutboxSettings>? configure,
        Action<IOutboxBuilder>? configureOutbox,
        string scopeKey = OutboxScope.DefaultKey)
    {
        ArgumentNullException.ThrowIfNull(services);

        OutboxScope.EnsureValid(scopeKey);

        InMemoryOutboxSettings settings = new();
        configure?.Invoke(settings);
        settings.Validate();

        // A disabled provider registers nothing: no store, no contracts, no schedule.
        if (!settings.Enabled)
            return services;

        // The core publisher takes a TimeProvider and the core never registers one; the provider owns
        // the clock it timestamps and leases with.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddKeyedSingleton(
            scopeKey,
            (sp, _) => new InMemoryOutboxStore(sp.GetRequiredService<TimeProvider>(), settings));

        services.AddSaOutbox(configureOutbox, scopeKey);

        return services.AddOutboxProvider(
            scopeKey,
            new OutboxProviderDescriptor
            {
                Name = "InMemory",
                Capabilities = OutboxProviderCapabilities.TenantDetection
                | OutboxProviderCapabilities.LockExtension
                | OutboxProviderCapabilities.Postponement,
                Version = typeof(Setup).Assembly.GetName().Version?.ToString(),
            },
            provider => provider
                .WithBulkWriter(sp => new InMemoryOutboxBulkWriter(
                    sp.GetRequiredKeyedService<InMemoryOutboxStore>(scopeKey),
                    sp.GetRequiredService<IOutboxMessageSerializer>()))
                .WithDeliveryManager(sp => new InMemoryOutboxDeliveryManager(
                    sp.GetRequiredKeyedService<InMemoryOutboxStore>(scopeKey),
                    sp.GetRequiredService<IOutboxContextFactory>(),
                    sp.GetRequiredService<IOutboxMessageSerializer>()))
                .WithTenantDetector(sp => new InMemoryOutboxTenantDetector(
                    sp.GetRequiredKeyedService<InMemoryOutboxStore>(scopeKey))));
    }

    /// <summary>
    /// Registers the in-memory Outbox with the default key (the classic single-provider form).
    /// </summary>
    /// <param name="services">The service collection to add outbox services to.</param>
    /// <param name="configure">An optional action to configure <see cref="InMemoryOutboxSettings"/>.</param>
    /// <param name="scopeKey">The outbox scope key; pass the same value to <c>AddSaOutbox</c> when the core is registered separately.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="scopeKey"/> is null, empty, whitespace, or the reserved default name.</exception>
    public static IServiceCollection AddSaOutboxUsingInMemory(
        this IServiceCollection services,
        Action<InMemoryOutboxSettings>? configure = null,
        string scopeKey = OutboxScope.DefaultKey)
            => services.AddSaOutboxUsingInMemory(configure, configureOutbox: null, scopeKey);
}
