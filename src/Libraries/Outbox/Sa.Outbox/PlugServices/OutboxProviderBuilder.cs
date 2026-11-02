using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Collects the three provider services for <see cref="OutboxProviderSetup.AddOutboxProvider"/>.
/// Each <c>With...</c> method replaces any earlier registration of that contract for the same
/// provider key, so the provider package owns the choice and a decorator package cannot leave a
/// stale implementation behind.
/// </summary>
public sealed class OutboxProviderBuilder
{
    private readonly IServiceCollection _services;
    private readonly string _providerKey;

    internal OutboxProviderBuilder(IServiceCollection services, string providerKey)
    {
        _services = services;
        _providerKey = providerKey;
    }

    /// <summary>The bulk writer type registered by this call, if any (used for the default version).</summary>
    internal Type? BulkWriterType { get; private set; }

    /// <summary>Whether the provider registered a bulk writer (type or factory overload).</summary>
    internal bool HasBulkWriter { get; private set; }

    /// <summary>Whether the provider registered a delivery manager.</summary>
    internal bool HasDeliveryManager { get; private set; }

    /// <summary>Whether the provider registered a tenant detector.</summary>
    internal bool HasTenantDetector { get; private set; }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxBulkWriter"/> under the provider key. Required:
    /// without it the provider cannot publish, and
    /// <see cref="OutboxProviderSetup.AddOutboxProvider"/> rejects the call.
    /// </summary>
    /// <typeparam name="T">The writer implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithBulkWriter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IOutboxBulkWriter
    {
        _services.RemoveAllKeyed<IOutboxBulkWriter>(_providerKey)
            .AddKeyedSingleton<IOutboxBulkWriter, T>(_providerKey);
        BulkWriterType = typeof(T);
        HasBulkWriter = true;
        return this;
    }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxBulkWriter"/> through a factory. Used by a keyed
    /// provider, whose implementation dependencies are themselves keyed and therefore cannot be
    /// constructor-injected (a keyed registration of a concrete type resolves its parameters
    /// unkeyed).
    /// </summary>
    /// <param name="factory">Creates the writer from the provider's keyed services.</param>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithBulkWriter(Func<IServiceProvider, IOutboxBulkWriter> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _services.RemoveAllKeyed<IOutboxBulkWriter>(_providerKey)
            .AddKeyedSingleton<IOutboxBulkWriter>(_providerKey, (sp, _) => factory(sp));
        HasBulkWriter = true;
        return this;
    }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxDeliveryManager"/> under the provider key.
    /// Optional: a publish-only provider omits it, and the registry then only accepts the
    /// installation while the application has no consumer groups for that provider.
    /// </summary>
    /// <typeparam name="T">The delivery manager implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithDeliveryManager<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IOutboxDeliveryManager
    {
        _services.RemoveAllKeyed<IOutboxDeliveryManager>(_providerKey)
            .AddKeyedSingleton<IOutboxDeliveryManager, T>(_providerKey);
        HasDeliveryManager = true;
        return this;
    }

    /// <summary>Registers the provider's <see cref="IOutboxDeliveryManager"/> through a factory.</summary>
    /// <param name="factory">Creates the manager from the provider's keyed services.</param>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithDeliveryManager(Func<IServiceProvider, IOutboxDeliveryManager> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _services.RemoveAllKeyed<IOutboxDeliveryManager>(_providerKey)
            .AddKeyedSingleton<IOutboxDeliveryManager>(_providerKey, (sp, _) => factory(sp));
        HasDeliveryManager = true;
        return this;
    }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxTenantDetector"/> under the provider key.
    /// Required when the descriptor declares <see cref="OutboxProviderCapabilities.TenantDetection"/>.
    /// </summary>
    /// <typeparam name="T">The detector implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithTenantDetector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class, IOutboxTenantDetector
    {
        _services.RemoveAllKeyed<IOutboxTenantDetector>(_providerKey)
            .AddKeyedSingleton<IOutboxTenantDetector, T>(_providerKey);
        HasTenantDetector = true;
        return this;
    }

    /// <summary>Registers the provider's <see cref="IOutboxTenantDetector"/> through a factory.</summary>
    /// <param name="factory">Creates the detector from the provider's keyed services.</param>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithTenantDetector(Func<IServiceProvider, IOutboxTenantDetector> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _services.RemoveAllKeyed<IOutboxTenantDetector>(_providerKey)
            .AddKeyedSingleton<IOutboxTenantDetector>(_providerKey, (sp, _) => factory(sp));
        HasTenantDetector = true;
        return this;
    }
}
