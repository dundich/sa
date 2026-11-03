using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// The single registration point of a storage provider. A provider package calls
/// <see cref="AddOutboxProvider(IServiceCollection, OutboxProviderDescriptor, Action{OutboxProviderBuilder})"/>
/// (default key) or the keyed overload from its own <c>AddSaOutboxUsing...</c> method, preferably
/// before <c>AddSaOutbox</c>. The core registers the same startup validator, so a consumer group
/// left without a provider fails the start even when no provider package was called at all.
/// </summary>
public static class OutboxProviderSetup
{
    /// <summary>
    /// Registers a storage provider under <paramref name="providerKey"/>: its descriptor, its three
    /// keyed services (through <paramref name="configure"/>) and the startup validation of the
    /// installation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is the provider identity the delivery pipeline carries in
    /// <see cref="Sa.Outbox.Delivery.OutboxConsumerSettings.ProviderKey"/>; for a provider package it
    /// is the outbox scope key. Several providers may be registered under different keys — that is
    /// keyed multi-provider support. The same key registered twice with the same descriptor name is
    /// idempotent (the marker is replaced); a different name under the same key is an error.
    /// </para>
    /// <para>
    /// The default key (<see cref="OutboxScope.DefaultKey"/>) additionally gets unkeyed aliases for
    /// the contracts it registered, so a single-provider application that resolves them without a
    /// key keeps working unchanged.
    /// </para>
    /// <para>
    /// The rest of the validation happens when the keyed <see cref="IOutboxProviderRegistry"/> is
    /// constructed — a <see cref="OutboxProviderValidationHostedService"/> (registered by this
    /// method and by <c>AddSaOutbox</c>) forces that for every registered key at host start and
    /// checks that every consumer group names a registered provider key.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="providerKey">The provider key (equal to the outbox scope key).</param>
    /// <param name="descriptor">The provider's identity and declared capabilities.</param>
    /// <param name="configure">Registers the provider's services on the builder.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key or <see cref="OutboxProviderDescriptor.Name"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different provider is already registered under the same key, or <paramref name="configure"/>
    /// registered no bulk writer.
    /// </exception>
    public static IServiceCollection AddOutboxProvider(
        this IServiceCollection services,
        string providerKey,
        OutboxProviderDescriptor descriptor,
        Action<OutboxProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("The provider name must not be blank.", nameof(descriptor));

        EnsureNoOtherProvider(services, providerKey, descriptor.Name);

        var builder = new OutboxProviderBuilder(services, providerKey);
        configure(builder);

        if (!builder.HasBulkWriter)
        {
            throw new InvalidOperationException(
                $"Outbox provider '{descriptor.Name}' registered no {nameof(IOutboxBulkWriter)}. " +
                "Call provider.WithBulkWriter<T>() inside AddOutboxProvider.");
        }

        // The version is informational; take it from the provider's own assembly unless the caller
        // pinned one, so a provider author does not have to duplicate the csproj value.
        OutboxProviderDescriptor final = descriptor.Version is null && builder.BulkWriterType is not null
            ? descriptor with { Version = builder.BulkWriterType.Assembly.GetName().Version?.ToString() }
            : descriptor;

        // Idempotent re-registration of the same provider under the same key: replace that key's
        // marker only — other providers keep theirs.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor service = services[i];

            if (service.ServiceType == typeof(OutboxProviderRegistration)
                && service.ImplementationInstance is OutboxProviderRegistration existing
                && string.Equals(existing.ProviderKey, providerKey, StringComparison.Ordinal))
            {
                services.RemoveAt(i);
            }
        }

        services.AddSingleton(new OutboxProviderRegistration(
            providerKey,
            final,
            HasDeliveryManager: builder.HasDeliveryManager,
            HasTenantDetector: builder.HasTenantDetector));

        services.TryAddKeyedSingleton<IOutboxProviderRegistry>(
            providerKey,
            (sp, _) => new OutboxProviderRegistry(providerKey, sp));

        if (string.Equals(providerKey, OutboxScope.DefaultKey, StringComparison.Ordinal))
        {
            RegisterDefaultAliases(services, providerKey);
            services.TryAddSingleton<IOutboxProviderRegistry>(
                sp => sp.GetRequiredKeyedService<IOutboxProviderRegistry>(OutboxScope.DefaultKey));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxProviderValidationHostedService>());

        return services;
    }

    /// <summary>
    /// Registers a storage provider under the default key (<see cref="OutboxScope.DefaultKey"/>),
    /// keeping the classic unkeyed service aliases.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptor">The provider's identity and declared capabilities.</param>
    /// <param name="configure">Registers the provider's services on the builder.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    public static IServiceCollection AddOutboxProvider(
        this IServiceCollection services,
        OutboxProviderDescriptor descriptor,
        Action<OutboxProviderBuilder> configure)
        => services.AddOutboxProvider(OutboxScope.DefaultKey, descriptor, configure);

    /// <summary>
    /// Unkeyed aliases for the default provider, so code written against the single-provider API
    /// (including test doubles) keeps resolving the contracts without a key. A contract the provider
    /// did not register gets no alias — resolving it keeps failing exactly as before.
    /// </summary>
    private static void RegisterDefaultAliases(IServiceCollection services, string providerKey)
    {
        services.TryAddSingleton<IOutboxBulkWriter>(
            sp => sp.GetRequiredKeyedService<IOutboxBulkWriter>(providerKey));

        if (IsRegisteredKeyed<IOutboxDeliveryManager>(services, providerKey))
        {
            services.TryAddSingleton<IOutboxDeliveryManager>(
                sp => sp.GetRequiredKeyedService<IOutboxDeliveryManager>(providerKey));
        }

        if (IsRegisteredKeyed<IOutboxTenantDetector>(services, providerKey))
        {
            services.TryAddSingleton<IOutboxTenantDetector>(
                sp => sp.GetRequiredKeyedService<IOutboxTenantDetector>(providerKey));
        }
    }

    private static bool IsRegisteredKeyed<T>(IServiceCollection services, string providerKey)
        => services.Any(d => d.ServiceType == typeof(T) && d.IsKeyedService
            && Equals(d.ServiceKey, providerKey));

    /// <summary>
    /// Rejects a second provider with a different name under the same key at registration time.
    /// Different keys are independent providers — that is the multi-provider case.
    /// </summary>
    private static void EnsureNoOtherProvider(IServiceCollection services, string providerKey, string name)
    {
        foreach (ServiceDescriptor service in services)
        {
            if (service.ServiceType != typeof(OutboxProviderRegistration)) continue;
            if (service.ImplementationInstance is not OutboxProviderRegistration existing) continue;
            if (!string.Equals(existing.ProviderKey, providerKey, StringComparison.Ordinal)) continue;
            if (string.Equals(existing.Descriptor.Name, name, StringComparison.Ordinal)) continue;

            throw new InvalidOperationException(
                $"Outbox provider '{existing.Descriptor.Name}' is already registered under the key " +
                $"'{providerKey}'; cannot add '{name}'. Use a different provider key.");
        }
    }
}
