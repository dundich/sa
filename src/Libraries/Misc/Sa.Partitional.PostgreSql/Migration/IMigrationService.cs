
namespace Sa.Partitional.PostgreSql;

/// <summary>
/// Service responsible for creating missing PostgreSQL partitions ahead of data arrival.
/// </summary>
public interface IMigrationService
{
    /// <summary>
    /// Gets a cancellation token that is triggered after a successful migration cycle completes.
    /// </summary>
    /// <remarks>The signal is one-shot: it stays triggered once a migration has completed, so it
    /// answers "has a migration ever completed", not "is one running right now".</remarks>
    CancellationToken OnMigrated { get; }

    /// <summary>
    /// Migrates all tables by creating any partitions that are missing for the current date range.
    /// </summary>
    /// <remarks>
    /// <para>Only one migration runs at a time. The caller's token is honoured while waiting for the
    /// migration lock (bounded by <see cref="MigrationScheduleSettings.WaitMigrationTimeout"/>); once
    /// the lock is taken the run is governed by its own deadline
    /// (<see cref="MigrationScheduleSettings.RunTimeout"/>) and is not interrupted by the caller's
    /// token.</para>
    /// <para>A run that did not finish returns <c>-1</c>, which means "not completed" - not a
    /// negative number of created partitions. The generated DDL is idempotent, so the next run picks
    /// up where this one stopped.</para>
    /// </remarks>
    /// <param name="cancellationToken">A token that cancels the wait for the migration lock.</param>
    /// <returns>The number of executed DDL statements, or <c>-1</c> if the run did not complete.</returns>
    Task<int> Migrate(CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates partitions only for the specified dates.
    /// </summary>
    /// <remarks>
    /// <para>Serialized against the background job and against other manual calls by the same
    /// migration lock. Unlike the parameterless overload this call waits for the lock as long as it
    /// takes - it is an explicit request, not a tick that may be skipped.</para>
    /// <para>The run is still bounded by <see cref="MigrationScheduleSettings.RunTimeout"/>. On top
    /// of that deadline an explicitly passed <paramref name="cancellationToken"/> stops the run: the
    /// caller asked for it. A run that was stopped returns <c>-1</c>; the DDL is idempotent, so a
    /// repeated call completes the remaining work.</para>
    /// </remarks>
    /// <param name="dates">An array of <see cref="DateTimeOffset"/> values for which to ensure partitions exist.</param>
    /// <param name="cancellationToken">A token that cancels both the wait for the migration lock and the run itself.</param>
    /// <returns>The number of executed DDL statements, or <c>-1</c> if the run did not complete.</returns>
    Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits asynchronously (up to <paramref name="timeout"/>) for an in-progress migration to complete.
    /// Returns immediately if a migration has already finished.
    /// </summary>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="cancellationToken">A token that stops waiting. Cancellation is reported as
    /// <see langword="false"/>, not as an exception.</param>
    /// <returns><c>true</c> if migration completed within the timeout; <c>false</c> otherwise.</returns>
    Task<bool> WaitMigration(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (OnMigrated.IsCancellationRequested) return Task.FromResult(true);

        return Wait();

        async Task<bool> Wait()
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            // The registration is disposed on every exit path. Leaving it alive would keep this
            // waiter subscribed to the migration's one-shot signal (and its task) for the lifetime
            // of the service, no matter whether the migration or the timeout won the race.
            using CancellationTokenRegistration registration = OnMigrated.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                completion);

            // Linked + CancelAfter instead of Task.Delay(timeout, ct): the delay task is never
            // completed, so nothing stays pending once this method returns, and the caller's
            // registration is released with the source.
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);

            Task finished = await Task
                .WhenAny(completion.Task, Task.Delay(Timeout.InfiniteTimeSpan, timer.Token))
                .ConfigureAwait(false);

            return finished == completion.Task;
        }
    }
}
