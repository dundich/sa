namespace Sa.Utils.WorkQueue;

/// <summary>
/// Stopping the queue: emergency reader cancellation, the real shutdown, draining
/// what is left in the channel, and single-flight disposal.
/// </summary>
/// <remarks>
/// Every path here ends in the same place: <c>Writer.TryComplete</c> plus
/// <see cref="DrainAndResetIdle(SaWorkDrainReason)"/>. A queue that reported
/// itself stopped while its channel was still open would never report the items
/// still inside it, would keep a non-zero pending count forever, and would leave
/// a producer parked in a waiting write with no way out. That pairing runs even
/// when the cancellation itself faulted, so it belongs in a
/// <see langword="finally"/>.
/// </remarks>
public sealed partial class SaWorkQueue<TInput>
{
    /// <summary>
    /// Emergency stop of the pool: cancels every reader, drops the items still in
    /// the buffer, and leaves the queue itself active. Restore the pool afterwards
    /// by setting <see cref="ConcurrencyLimit"/>.
    /// </summary>
    public void ForceCancelReaders()
    {
        if (!IsEnabled) return;

        var tasks = CancelAndTrackReaders();

        if (tasks.Length > 0)
        {
            // Bounded wait: a processor that ignores cancellation must not block the
            // calling thread indefinitely. A timeout is not an error here — the reader
            // is being abandoned on purpose, and the caller is told by the return value
            // rather than by an exception.
            WaitAllBounded(tasks, _shutdownTimeout);
        }

        DrainAndResetIdle(SaWorkDrainReason.ForceCancel);
    }

    /// <inheritdoc cref="ForceCancelReaders" />
    /// <param name="timeout">
    /// How long to wait for the cancelled readers to unwind. <see langword="null"/>
    /// waits for as long as they take, which is unbounded by design — use
    /// <see cref="_shutdownTimeout"/>'s value as a starting point.
    /// </param>
    /// <param name="ct">Cancels the wait, not the readers.</param>
    /// <remarks>
    /// Only the timeout path keeps the buffer. When an explicit <paramref name="timeout"/>
    /// elapses — or <paramref name="ct"/> ends the wait — the readers were asked to stop and
    /// are still unwinding, so the items behind them were never rejected by anybody and a
    /// pool re-armed afterwards can still take them. Dropping work that nobody asked to drop
    /// is worse than leaving it waiting, so the buffer is kept and the shortfall is logged
    /// instead. <see cref="IsIdle"/> stays <see langword="false"/> — truthfully, there is
    /// work left — and a later <see cref="WaitForIdleAsync"/> that returns without progress
    /// says so in a log line rather than silently.
    /// <para>
    /// On success — and when there is no reader left to wait for — this drains the buffer
    /// exactly like <see cref="ForceCancelReaders"/>: "stop the pool and throw away what is
    /// left" is what a completed force-cancel means.
    /// </para>
    /// </remarks>
    public async Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var tasks = CancelAndTrackReaders();

        if (tasks.Length > 0)
        {
            try
            {
                if (timeout is { } t)
                    await Task.WhenAll(tasks).WaitAsync(t, _timeProvider, ct).ConfigureAwait(false);
                else
                    await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Deliberately no drain. The readers were asked to stop and are still
                // unwinding; the items behind them are still processable. Rethrown so
                // the caller learns the wait expired rather than inferring success.
                SaWorkQueueLogMessages.LogForceCancelTimedOut(
                    _logger, timeout!.Value.TotalSeconds, CountUnfinished(tasks), PendingCount);
                throw;
            }
            catch (OperationCanceledException)
            {
                // Same reasoning: the caller's token ended the wait, it did not change
                // what the buffer is worth.
                SaWorkQueueLogMessages.LogForceCancelWaitCancelled(
                    _logger, CountUnfinished(tasks), PendingCount);
                throw;
            }
        }

        DrainAndResetIdle(SaWorkDrainReason.ForceCancel);
    }

    /// <summary>
    /// Empties the channel, reporting a terminal status for every item dropped, and
    /// releases the idle signal if the pool is already gone.
    /// </summary>
    /// <param name="reason">
    /// Why the items are being dropped. The status is
    /// <see cref="SaWorkStatus.Faulted"/> either way — consumers already treat it
    /// as an error — but the exception text has to match the cause: a force-cancel
    /// does not shut the queue down, and saying so would be misleading.
    /// </param>
    private void DrainAndResetIdle(SaWorkDrainReason reason)
    {
        while (_queue.Reader.TryRead(out var item))
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                // The producer gave up before any reader reached this item, so
                // nothing has reported it yet. A reader picking it up would have
                // reported Aborted, so do the same here and keep the "every
                // accepted item gets a terminal status" contract.
                OnStatusChanged(item.Input, SaWorkStatus.Aborted, ThrowHelper.CallerCancelledException());
            }
            else
            {
                // A fresh exception per item, so an observer can tell which work
                // item was dropped (a shared singleton made them all
                // reference-identical).
                OnStatusChanged(item.Input, SaWorkStatus.Faulted, ThrowHelper.DroppedException(reason));
            }

            MarkInactive();
        }

        ResetPendingIfAllReadersDone();
    }

    /// <inheritdoc />
    public async Task ShutdownAsync()
    {
        if (Interlocked.CompareExchange(ref _state, (int)QueueState.Shutdown, (int)QueueState.Active) != (int)QueueState.Active)
        {
            // Someone else owns the shutdown. Waiting for them is what makes "idempotent"
            // mean the same thing to every caller: when this method returns, the readers are
            // gone and what the buffer held has been reported. The exception is a call made
            // by the shutdown itself — a status callback asking to stop the queue again —
            // which must not wait for a completion only the flow it interrupts can signal.
            if (!IsShuttingDownHere)
            {
                await _shutdownCompleted.Task.ConfigureAwait(false);
            }

            return;
        }

        ShutdownDepth.Value++;

        try
        {
            try
            {
                await _shutdownCts.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
            }

            // See the type remarks: closing and draining must happen even if the
            // cancellation above faulted.
            _queue.Writer.TryComplete();

            try
            {
                await WaitForReadersToCompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
            }
            finally
            {
                DrainAndResetIdle(SaWorkDrainReason.Shutdown);
            }
        }
        finally
        {
            ShutdownDepth.Value--;
            _shutdownCompleted.TrySetResult();
        }
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _state, (int)QueueState.Shutdown, (int)QueueState.Active) != (int)QueueState.Active)
        {
            // See ShutdownAsync: a caller that finds the shutdown already running waits for
            // it, unless that shutdown is the one calling.
            if (!IsShuttingDownHere)
            {
                _shutdownCompleted.Task.Wait();
            }

            return;
        }

        ShutdownDepth.Value++;

        try
        {
            try
            {
                _shutdownCts.Cancel();
            }
            catch (Exception ex)
            {
                SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
            }

            _queue.Writer.TryComplete();

            try
            {
                WaitForReadersToComplete();
            }
            catch (Exception ex)
            {
                SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
            }
            finally
            {
                DrainAndResetIdle(SaWorkDrainReason.Shutdown);
            }
        }
        finally
        {
            ShutdownDepth.Value--;
            _shutdownCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for every tracked reader to finish, bounded by
    /// <see cref="_shutdownTimeout"/>; a reader that ignores cancellation is logged
    /// and let go rather than holding up shutdown (and therefore
    /// <see cref="DisposeAsync"/>).
    /// </summary>
    private async Task WaitForReadersToCompleteAsync()
    {
        Task[] tasks;
        lock (_readersSync)
        {
            tasks = SnapshotReaderTasks();
        }

        if (tasks.Length == 0) return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(_shutdownTimeout, _timeProvider, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ThrowHelper.ReadersTimeout(asynchronous: true, _shutdownTimeout.TotalSeconds));
        }
    }

    /// <summary>
    /// Blocking counterpart of <see cref="WaitForReadersToCompleteAsync"/>, used by
    /// <see cref="Shutdown"/>.
    /// </summary>
    private void WaitForReadersToComplete()
    {
        Task[] tasks;
        lock (_readersSync)
        {
            tasks = SnapshotReaderTasks();
        }

        if (tasks.Length == 0) return;

        if (!WaitAllBounded(tasks, _shutdownTimeout))
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ThrowHelper.ReadersTimeout(asynchronous: false, _shutdownTimeout.TotalSeconds));
        }
    }

    /// <summary>
    /// Blocks until every task finishes or <paramref name="timeout"/> elapses on the
    /// injected clock. Returns <see langword="false"/> on timeout.
    /// </summary>
    /// <remarks>
    /// <c>Task.WaitAll(tasks, timeout)</c> has no <see cref="TimeProvider"/> overload, so
    /// the bounded wait is expressed as a timed <c>WaitAsync</c> and then blocked on.
    /// Blocking on the <em>task</em> rather than awaiting it keeps the caller's ambient
    /// synchronization context out of it: the wait completes from a timer callback, so
    /// nothing is posted back to the thread that is already parked here.
    /// <para>
    /// The timeout is deliberately not an exception, matching
    /// <c>Task.WaitAll(tasks, timeout)</c> returning <see langword="false"/>. The one
    /// difference is a task that faults: it surfaces as its own exception rather than
    /// wrapped in <see cref="AggregateException"/>.
    /// </para>
    /// </remarks>
    private bool WaitAllBounded(Task[] tasks, TimeSpan timeout)
    {
        try
        {
            Task.WhenAll(tasks)
                .WaitAsync(timeout, _timeProvider, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// How many of the given tasks are still running. For diagnostics only — the
    /// answer is a snapshot that a reader can invalidate a nanosecond later, and
    /// nothing branches on it.
    /// </summary>
    private static int CountUnfinished(Task[] tasks)
    {
        var count = 0;

        foreach (var task in tasks)
        {
            if (!task.IsCompleted) count++;
        }

        return count;
    }

    /// <summary>Marks the queue disposed. Runs once, on the thread that won the dispose race.</summary>
    /// <remarks>
    /// <c>_shutdownCts</c> is deliberately left undisposed. Disposing it bought nothing — the
    /// linked sources the readers own are released by <see cref="Reader.Release"/>, and an
    /// undisposed source with no pending timer holds no unmanaged state once it is garbage —
    /// while it opened a window: a reader registered by a concurrent
    /// <see cref="ConcurrencyLimit"/> change is created with <c>_shutdownCts.Token</c>, which
    /// throws <see cref="ObjectDisposedException"/> once the source is disposed. That turned a
    /// setter documented to "do nothing" into a throwing one, and left the readers registered
    /// by that call unlaunched.
    /// </remarks>
    private void CompleteDispose()
        => Interlocked.Exchange(ref _state, (int)QueueState.Disposed);

    /// <inheritdoc />
    public void Dispose()
        => RunDispose(Shutdown);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
        => await RunDisposeAsync(ShutdownAsync).ConfigureAwait(false);

    /// <summary>
    /// Single-flight dispose: the first caller becomes the winner, runs
    /// <paramref name="shutdown"/>, and tears down the shared state; any concurrent
    /// loser waits for the winner rather than racing it.
    /// </summary>
    /// <remarks>
    /// The winner must not be beaten to the teardown: a loser must never observe a
    /// half-disposed queue, and the shutdown it would otherwise race is still reporting
    /// what it dropped.
    /// </remarks>
    private void RunDispose(Action shutdown)
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            // A call made from inside the shutdown the winner is running would wait on
            // itself; see ShutdownDepth.
            if (!IsShuttingDownHere)
            {
                _disposeCompleted.Task.Wait();
            }

            return;
        }

        try
        {
            shutdown();
        }
        finally
        {
            CompleteDispose();
            _disposeCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="RunDispose(Action)"/>: a losing
    /// caller awaits the winner instead of blocking a thread.
    /// </summary>
    private async Task RunDisposeAsync(Func<Task> shutdown)
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            if (!IsShuttingDownHere)
            {
                await _disposeCompleted.Task.ConfigureAwait(false);
            }

            return;
        }

        try
        {
            await shutdown().ConfigureAwait(false);
        }
        finally
        {
            CompleteDispose();
            _disposeCompleted.TrySetResult();
        }
    }
}
