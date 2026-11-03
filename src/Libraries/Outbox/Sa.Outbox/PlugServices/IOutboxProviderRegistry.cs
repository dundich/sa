namespace Sa.Outbox.PlugServices;

/// <summary>
/// The storage provider installed in this container: identity, capabilities and the registry that
/// proves the installation is complete.
/// </summary>
/// <remarks>
/// Registered once by <c>AddOutboxProvider</c> (in the provider package). Constructing the registry
/// validates the installation — one provider, a resolvable bulk writer, a delivery manager when
/// consumer groups exist, and a detector when the <see cref="OutboxProviderCapabilities.TenantDetection"/>
/// capability is declared — so a broken installation fails at startup instead of on the first
/// publish or delivery tick.
/// </remarks>
public interface IOutboxProviderRegistry
{
    /// <summary>The installed provider's descriptor.</summary>
    OutboxProviderDescriptor Descriptor { get; }
}
