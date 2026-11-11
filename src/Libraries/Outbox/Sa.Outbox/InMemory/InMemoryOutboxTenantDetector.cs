using Sa.Outbox.PlugServices;

namespace Sa.Outbox.InMemory;

/// <summary>
/// Discovers the tenants that still hold deliverable messages in the store.
/// </summary>
/// <remarks>
/// The in-memory equivalent of the PostgreSQL and Redis detectors: a tenant that never published has
/// nothing to deliver, so it is indistinguishable from a missing one. Without a detector the delivery
/// loop would not start at all unless the application listed tenants explicitly.
/// </remarks>
internal sealed class InMemoryOutboxTenantDetector(InMemoryOutboxStore store) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public ValueTask<int[]> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(store.GetTenantIds());
    }
}
