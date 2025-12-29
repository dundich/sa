
namespace Sa.Outbox;

/// <summary>
/// Tenant provider for partitional support.
/// </summary>
public interface ITenantSource
{
    /// <summary>
    /// Retrieves an array of tenant IDs from settings or auto detect in db.
    /// </summary>
    ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken);
}
