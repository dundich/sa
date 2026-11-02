using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// The single registration point of a storage provider. A provider package calls
/// <see cref="AddOutboxProvider"/> from its own <c>AddSaOutboxUsing...</c> method, before
/// <c>AddSaOutbox</c>, so the startup validator runs before the delivery schedules.
/// </summary>
public static class OutboxProviderSetup
{
    /// <summary>
    /// Registers a storage provider: its descriptor, its three services (through
    /// <paramref name="configure"/>) and the startup validation of the installation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call is the only place that knows the provider's identity, so it is also the place where
    /// obvious mistakes fail immediately: a blank name, a missing bulk writer and a second provider
    /// with a different name. Registering the same provider again (same <see cref="OutboxProviderDescriptor.Name"/>)
    /// is idempotent — the marker is replaced, which keeps repeated configuration calls harmless.
    /// </para>
    /// <para>
    /// The rest of the validation happens when <see cref="IOutboxProviderRegistry"/> is constructed:
    /// a <see cref="OutboxProviderValidationHostedService"/> forces that at host start, and the
    /// delivery job settings build re-checks it before the first tick.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptor">The provider's identity and declared capabilities.</param>
    /// <param name="configure">Registers the provider's services on the builder.</param>
    /// <returns>The updated <see cref="IServiceCollection"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="OutboxProviderDescriptor.Name"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">
    /// A different provider is already registered, or <paramref name="configure"/> registered no
    /// bulk writer.
    /// </exception>
    public static IServiceCollection AddOutboxProvider(
        this IServiceCollection services,
        OutboxProviderDescriptor descriptor,
        Action<OutboxProviderBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(configure);

        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new ArgumentException("The provider name must not be blank.", nameof(descriptor));

        EnsureNoOtherProvider(services, descriptor.Name);

        var builder = new OutboxProviderBuilder(services);
        configure(builder);

        if (builder.BulkWriterType is null)
        {
            throw new InvalidOperationException(
                $"Outbox provider '{descriptor.Name}' registered no {nameof(IOutboxBulkWriter)}. " +
                "Call provider.WithBulkWriter<T>() inside AddOutboxProvider.");
        }

        // The version is informational; take it from the provider's own assembly unless the caller
        // pinned one, so a provider author does not have to duplicate the csproj value.
        OutboxProviderDescriptor final = descriptor.Version is null
            ? descriptor with { Version = builder.BulkWriterType.Assembly.GetName().Version?.ToString() }
            : descriptor;

        // Idempotent re-registration of the same provider: keep exactly one marker.
        services.RemoveAll<OutboxProviderRegistration>();
        services.AddSingleton(new OutboxProviderRegistration(final));

        services.TryAddSingleton<IOutboxProviderRegistry, OutboxProviderRegistry>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxProviderValidationHostedService>());

        return services;
    }

    /// <summary>
    /// Rejects a second provider with a different name at registration time — the alternative is
    /// the old silent "last registration wins", which left the first provider's schedules and
    /// partitional module running against storage nobody uses.
    /// </summary>
    private static void EnsureNoOtherProvider(IServiceCollection services, string name)
    {
        foreach (ServiceDescriptor service in services)
        {
            if (service.ServiceType != typeof(OutboxProviderRegistration)) continue;
            if (service.ImplementationInstance is not OutboxProviderRegistration existing) continue;

            if (string.Equals(existing.Descriptor.Name, name, StringComparison.Ordinal)) continue;

            throw new InvalidOperationException(
                $"Outbox provider '{existing.Descriptor.Name}' is already registered; cannot add " +
                $"'{name}'. The Outbox supports one storage provider per container.");
        }
    }
}
