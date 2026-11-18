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
public sealed record OutboxProviderDescriptor
{
    /// <summary>The stable provider name, for example <c>PostgreSql</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Optional capabilities the storage really provides.</summary>
    public OutboxProviderCapabilities Capabilities { get; init; } = OutboxProviderCapabilities.None;

    /// <summary>Informational version for logs and diagnostics.</summary>
    public string? Version { get; init; }

    /// <summary>Whether every capability of <paramref name="capability"/> is declared.</summary>
    /// <param name="capability">The capability (or a mask of capabilities) to test.</param>
    /// <returns><see langword="true"/> when all requested flags are present.</returns>
    public bool Supports(OutboxProviderCapabilities capability)
        => (Capabilities & capability) == capability;
}
