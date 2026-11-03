namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Represents a service interface for cleaning up Outbox message parts.
/// This interface defines methods for performing cleanup operations on message parts.
/// </summary>
public interface IPartCleanupService
{
    /// <summary>
    /// Asynchronously cleans up Outbox message parts.
    /// This method removes parts that are no longer needed based on the retention policy.
    /// </summary>
    /// <remarks>
    /// Dropping partitions behind the cache's back does not invalidate the cached partition metadata,
    /// so a call made outside the background job leaves a stale snapshot in place until it expires
    /// (see <see cref="PartCacheSettings.CacheTtl"/>). Prefer the scheduled job, or run
    /// <see cref="IPartitionManager.Migrate"/> and the cleanup from the same place, when the same
    /// process is expected to see the change right away.
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of parts cleaned up.</returns>
    Task<int> Clean(CancellationToken cancellationToken);

    /// <summary>
    /// Asynchronously cleans up Outbox message parts up to a specified date.
    /// This method removes parts that are older than the provided date.
    /// </summary>
    /// <remarks>Cached partition metadata is not invalidated - see the note on the other overload.</remarks>
    /// <param name="toDate">The date up to which parts should be cleaned up.</param>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of parts cleaned up.</returns>
    Task<int> Clean(DateTimeOffset toDate, CancellationToken cancellationToken);
}
