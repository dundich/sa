
namespace Sa.Outbox.Partitional;

/// <summary>
/// Represents a cache that provides partitional support.
/// </summary>
public interface IPartitionalSupportCache
{
    /// <summary>
    /// Retrieves an array of tenant IDs from the cache.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing an array of tenant IDs.</returns>
    ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken);
}
