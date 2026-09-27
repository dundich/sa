using Microsoft.Extensions.Logging;
using Sa.Extensions;

namespace Sa.Partitional.PostgreSql.Migration;

/// <summary>
/// Runs partition migrations under their own deadline, one run at a time.
/// </summary>
/// <remarks>
/// A run that has obtained the lock is driven by <see cref="MigrationScheduleSettings.RunTimeout"/>,
/// not by the caller's token: the DDL emitted by the migration is idempotent, so finishing a run
/// is cheaper than restarting it, and a started run must not be cut in half by somebody else's
/// cancellation. The caller's token is honoured while waiting for the lock.
/// </remarks>
internal sealed partial class PartMigrationService(
    IPartRepository repository
    , TimeProvider timeProvider
    , MigrationScheduleSettings settings
    // Required, not optional: the [LoggerMessage] methods dereference it, so a null logger
    // turned every call into a NullReferenceException before the migration even started.
    , ILogger<PartMigrationService> logger) : IMigrationService, IDisposable
{
    private readonly SemaphoreSlim _migrationLock = new(1, 1);

    /// <summary>
    /// One-shot "a migration has completed" signal for <see cref="OnMigrated"/>.
    /// </summary>
    private readonly CancellationTokenSource _migratedCts = new();

    public CancellationToken OnMigrated => _migratedCts.Token;

    public void Dispose()
    {
        _migratedCts.Dispose();
        _migrationLock.Dispose();
    }

    public async Task<int> Migrate(DateTimeOffset[] dates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dates);

        // A manual call is an explicit request, so it waits for the lock without a timeout of its
        // own - and it is serialized against the background job instead of running beside it.
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Run(dates, cancellationToken, honorCallerToken: true).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLock();
        }
    }

    public async Task<int> Migrate(CancellationToken cancellationToken = default)
    {
        // Bounded wait: a tick that cannot get the lock is skipped, not queued.
        if (!await _migrationLock.WaitAsync(settings.WaitMigrationTimeout, cancellationToken).ConfigureAwait(false))
        {
            LogSkippedBusy(settings.WaitMigrationTimeout);
            return -1;
        }

        try
        {
            DateTimeOffset now = timeProvider.GetUtcNow().StartOfDay();
            DateTimeOffset[] dates = [.. Enumerable
                .Range(0, settings.ForwardDays)
                .Select(i => now.AddDays(i))];

            // The background job owns no token of its own: the scheduler's token is the host's,
            // and a started run has to reach the end.
            return await Run(dates, cancellationToken, honorCallerToken: false).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLock();
        }
    }

    /// <summary>
    /// Executes the migration for <paramref name="dates"/> and reports how many statements were run.
    /// </summary>
    /// <param name="dates">The dates to ensure partitions for.</param>
    /// <param name="callerToken">The caller's token.</param>
    /// <param name="honorCallerToken">
    /// <see langword="true"/> for a manual call, where the caller explicitly asked for a token;
    /// <see langword="false"/> for the background job, where the token belongs to the host.
    /// </param>
    /// <returns>
    /// The number of statements executed, or <c>-1</c> when the run did not finish
    /// (the deadline elapsed or it was cancelled) - never "a negative number of partitions".
    /// </returns>
    private async Task<int> Run(
        DateTimeOffset[] dates
        , CancellationToken callerToken
        , bool honorCallerToken)
    {
        using var deadlineCts = new CancellationTokenSource(settings.RunTimeout);

        // The repository (and through it user IPartTableMigrationSupport.GetParts code) only ever
        // sees a token owned by this library.
        using CancellationTokenSource runCts = honorCallerToken
            ? CancellationTokenSource.CreateLinkedTokenSource(deadlineCts.Token, callerToken)
            : deadlineCts;

        CancellationToken runToken = runCts.Token;

        LogMigrating(dates.Length, settings.RunTimeout);

        try
        {
            int result = await repository.Migrate(dates, runToken).ConfigureAwait(false);

            SignalMigrated();

            LogMigrated(result);
            return result;
        }
        catch (OperationCanceledException ex)
        {
            // The source state, not the token carried by the exception: a run that honours the
            // caller's token is driven by a *linked* source, so Npgsql reports the linked token and
            // an elapsed deadline would otherwise be logged as "not the deadline".
            LogNotCompleted(deadlineCts.IsCancellationRequested, ex);
            return -1;
        }
        catch (Exception ex)
        {
            LogMigrationFailed(ex);
            throw;
        }
    }

    /// <summary>
    /// Releases the run lock, tolerating a service that was disposed while a run was in flight.
    /// </summary>
    /// <remarks>
    /// Same shutdown race as <see cref="SignalMigrated"/>: the host disposes this service on its way
    /// out, which can happen while a manual run still holds the lock. Releasing a disposed semaphore
    /// would turn an orderly shutdown into an unhandled exception on the way out of <c>Migrate</c>.
    /// </remarks>
    private void ReleaseLock()
    {
        try
        {
            _migrationLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // The service was disposed while this run finished - the lock died with it.
        }
    }

    /// <summary>
    /// Raises the one-shot <see cref="OnMigrated"/> signal.
    /// </summary>
    /// <remarks>
    /// A manual <see cref="Migrate(CancellationToken)"/> can still be in flight when the host starts
    /// shutting down and disposes this service, so the source may already be gone. That is the normal
    /// end of a run, not a failure: an <see cref="ObjectDisposedException"/> here would otherwise
    /// escape as a migration error and abort the job on the way out.
    /// </remarks>
    private void SignalMigrated()
    {
        try
        {
            _migratedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The service was disposed while this run finished - nothing left to signal.
        }
    }

    [LoggerMessage(
        EventId = 601,
        Level = LogLevel.Debug,
        Message = "Migration started for {DatesCount} date(s), run deadline {RunTimeout}.")]
    partial void LogMigrating(int datesCount, TimeSpan runTimeout);

    [LoggerMessage(
        EventId = 602,
        Level = LogLevel.Information,
        Message = "Migration completed, {Statements} statement(s) executed.")]
    partial void LogMigrated(int statements);

    [LoggerMessage(
        EventId = 603,
        Level = LogLevel.Warning,
        Message = "Migration did not complete and returned -1. DeadlineElapsed: {DeadlineElapsed}.")]
    partial void LogNotCompleted(bool deadlineElapsed, Exception exception);

    [LoggerMessage(
        EventId = 604,
        Level = LogLevel.Error,
        Message = "Migration failed.")]
    partial void LogMigrationFailed(Exception exception);

    [LoggerMessage(
        EventId = 605,
        Level = LogLevel.Debug,
        Message = "Migration skipped: the previous run still holds the lock after {WaitTimeout}, returning -1.")]
    partial void LogSkippedBusy(TimeSpan waitTimeout);
}
