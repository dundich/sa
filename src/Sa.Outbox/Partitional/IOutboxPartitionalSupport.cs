
namespace Sa.Outbox.Partitional;

/// <summary>
/// Represents a pair of tenant identifier and part information in the Outbox system.
/// This record is used to associate a tenant with a specific part of the Outbox message.
/// </summary>
/// <param name="TenantId">The unique identifier for the tenant.</param>
/// <param name="Part">The part identifier associated with the tenant.</param>
public record struct OutboxTenantPartPair(int TenantId, string Part);

/// <summary>
/// Represents an interface for supporting partitioning in the Outbox processing system.
/// This interface defines a method for retrieving tenant-part pairs.
/// </summary>
public interface IOutboxPartitionalSupport
{
    /// <summary>
    /// Asynchronously retrieves a collection of tenant-part pairs.
    /// This method can be used to get the current mapping of tenants to their respective parts.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation, containing a read-only collection of <see cref="OutboxTenantPartPair"/>.</returns>
    Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPartValues(CancellationToken cancellationToken);
}
