using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PlugServices;

/// <summary>
/// Collects the three provider services for <see cref="OutboxProviderSetup.AddOutboxProvider"/>.
/// Each <c>With...</c> method replaces any earlier registration of that contract, so the provider
/// package owns the choice and a decorator package cannot leave a stale implementation behind.
/// </summary>
public sealed class OutboxProviderBuilder
{
    private readonly IServiceCollection _services;

    internal OutboxProviderBuilder(IServiceCollection services)
    {
        _services = services;
    }

    /// <summary>The bulk writer type registered by this call, if any (used for the default version).</summary>
    internal Type? BulkWriterType { get; private set; }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxBulkWriter"/>. Required: without it the provider
    /// cannot publish, and <see cref="OutboxProviderSetup.AddOutboxProvider"/> rejects the call.
    /// </summary>
    /// <typeparam name="T">The writer implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithBulkWriter<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IOutboxBulkWriter
    {
        _services.RemoveAll<IOutboxBulkWriter>().AddSingleton<IOutboxBulkWriter, T>();
        BulkWriterType = typeof(T);
        return this;
    }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxDeliveryManager"/>. Optional: a publish-only
    /// provider omits it, and the registry then only accepts the installation while the application
    /// has no consumer groups.
    /// </summary>
    /// <typeparam name="T">The delivery manager implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithDeliveryManager<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IOutboxDeliveryManager
    {
        _services.RemoveAll<IOutboxDeliveryManager>().AddSingleton<IOutboxDeliveryManager, T>();
        return this;
    }

    /// <summary>
    /// Registers the provider's <see cref="IOutboxTenantDetector"/>. Required when the descriptor
    /// declares <see cref="OutboxProviderCapabilities.TenantDetection"/>.
    /// </summary>
    /// <typeparam name="T">The detector implementation, resolved from DI as a singleton.</typeparam>
    /// <returns>The same builder for chaining.</returns>
    public OutboxProviderBuilder WithTenantDetector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IOutboxTenantDetector
    {
        _services.RemoveAll<IOutboxTenantDetector>().AddSingleton<IOutboxTenantDetector, T>();
        return this;
    }
}
