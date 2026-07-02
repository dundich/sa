
namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Service responsible for creating missing PostgreSQL partitions ahead of data arrival.
/// </summary>
public interface IMigrationService
{
    /// <summary>
    /// Gets a cancellation token that is triggered after a successful migration cycle completes.
    /// </summary>
    CancellationToken OnMigrated { get; }

    /// <summary>
    /// Migrates all tables by creating any partitions that are missing for the current date range.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of partitions created (0 if everything was already up to date).</returns>
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates partitions only for the specified dates.
    /// </summary>
    /// <param name="dates">An array of <see cref="DateTimeOffset"/> values for which to ensure partitions exist.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of partitions created.</returns>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits synchronously (up to <paramref name="timeout"/>) for an in-progress migration to complete.
    /// Returns immediately if a migration has already finished.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns><c>true</c> if migration completed within the timeout; <c>false</c> otherwise.</returns>
    Task<bool> WaitMigration(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (OnMigrated.IsCancellationRequested) return Task.FromResult(true);

        var tcs = new TaskCompletionSource();
        OnMigrated.Register(() => tcs.SetResult());
        return Task.Run(() => Task.WaitAny(tcs.Task, Task.Delay(timeout, cancellationToken)) == 0);
    }
}
