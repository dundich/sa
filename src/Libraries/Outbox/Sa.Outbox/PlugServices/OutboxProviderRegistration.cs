namespace Sa.Outbox.PlugServices;

/// <summary>
/// DI marker written by <c>AddOutboxProvider</c>. Kept separate from the keyed contract
/// registrations so the duplicate check can inspect the <see cref="IServiceCollection"/> before the
/// container is built, so a keyed registry can tell "no provider for this key" apart from
/// "provider registered but broken", and so validation needs no service construction.
/// </summary>
/// <param name="ProviderKey">The provider key (equal to the outbox scope key).</param>
/// <param name="Descriptor">The provider's identity and declared capabilities.</param>
/// <param name="HasDeliveryManager">Whether the provider registered a delivery manager.</param>
/// <param name="HasTenantDetector">Whether the provider registered a tenant detector.</param>
internal sealed record OutboxProviderRegistration(
    string ProviderKey,
    OutboxProviderDescriptor Descriptor,
    bool HasDeliveryManager,
    bool HasTenantDetector);
