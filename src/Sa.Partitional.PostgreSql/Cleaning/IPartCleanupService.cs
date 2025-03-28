
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
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of parts cleaned up.</returns>
    Task<int> Clean(CancellationToken cancellationToken);

    /// <summary>
    /// Asynchronously cleans up Outbox message parts up to a specified date.
    /// This method removes parts that are older than the provided date.
    /// </summary>
    /// <param name="toDate">The date up to which parts should be cleaned up.</param>
    /// <param name="cancellationToken">A cancellation token to signal the operation's cancellation.</param>
    /// <returns>A task representing the asynchronous operation, containing the number of parts cleaned up.</returns>
    Task<int> Clean(DateTimeOffset toDate, CancellationToken cancellationToken);
}