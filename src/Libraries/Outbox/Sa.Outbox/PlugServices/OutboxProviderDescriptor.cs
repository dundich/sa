namespace Sa.Outbox.PlugServices;

/// <summary>
/// Identity and declared capabilities of the storage provider installed in the container.
/// </summary>
/// <remarks>
/// <para>
/// A descriptor is metadata only: it owns no services and no resources. The three provider
/// contracts stay in DI (<see cref="IOutboxBulkWriter"/>, <see cref="IOutboxDeliveryManager"/>,
/// <see cref="IOutboxTenantDetector"/>) and are resolved exactly as before — the descriptor exists
/// so the core can validate the installation at startup, log what is running, and answer
/// diagnostics without a service locator.
/// </para>
/// <para>
/// One provider per container (see <c>AddOutboxProvider</c>). The <see cref="Name"/> is
/// stable and human-facing ("PostgreSql", "Redis"); it is also the value future keyed multi-provider
/// registrations would use, which is why it must not be a type name.
/// </para>
/// </remarks>
/// <param name="Name">The stable provider name, for example <c>PostgreSql</c>. Must not be blank.</param>
/// <param name="Capabilities">Optional capabilities the storage really provides.</param>
/// <param name="Version">
/// Informational version for logs and diagnostics. When omitted,
/// <c>AddOutboxProvider</c> fills it from the bulk writer's assembly.
/// </param>
public sealed record OutboxProviderDescriptor(
    string Name,
    OutboxProviderCapabilities Capabilities = OutboxProviderCapabilities.None,
    string? Version = null)
{
    /// <summary>Whether every capability of <paramref name="capability"/> is declared.</summary>
    /// <param name="capability">The capability (or a mask of capabilities) to test.</param>
    /// <returns><see langword="true"/> when all requested flags are present.</returns>
    public bool Supports(OutboxProviderCapabilities capability)
        => (Capabilities & capability) == capability;
}
