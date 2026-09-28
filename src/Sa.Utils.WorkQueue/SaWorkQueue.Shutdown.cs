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
            // calling thread indefinitely.
            Task.WaitAll(tasks, _shutdownTimeout);
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
    public async Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var tasks = CancelAndTrackReaders();

        if (tasks.Length > 0)
        {
            if (timeout is { } t)
                await Task.WhenAll(tasks).WaitAsync(t, ct).ConfigureAwait(false);
            else
                await Task.WhenAll(tasks).ConfigureAwait(false);
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
            return;

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

    /// <inheritdoc />
    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _state, (int)QueueState.Shutdown, (int)QueueState.Active) != (int)QueueState.Active)
            return;

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
            await Task.WhenAll(tasks).WaitAsync(_shutdownTimeout).ConfigureAwait(false);
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

        if (!Task.WaitAll(tasks, _shutdownTimeout))
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ThrowHelper.ReadersTimeout(asynchronous: false, _shutdownTimeout.TotalSeconds));
        }
    }

    private void CompleteDispose()
    {
        if (Interlocked.Exchange(ref _state, (int)QueueState.Disposed) != (int)QueueState.Disposed)
        {
            _shutdownCts.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
        => RunDispose(Shutdown);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await RunDisposeAsync(async () =>
        {
            if (IsEnabled)
            {
                await ShutdownAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-flight dispose: the first caller becomes the winner, runs
    /// <paramref name="shutdown"/>, and tears down the shared state; any concurrent
    /// loser waits for the winner rather than racing it.
    /// </summary>
    /// <remarks>
    /// The winner must not be beaten to the teardown: <c>_shutdownCts</c> must not
    /// be disposed out from under a still-running shutdown, and a loser must never
    /// observe a half-disposed queue.
    /// </remarks>
    private void RunDispose(Action shutdown)
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            _disposeCompleted.Task.Wait();
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
            await _disposeCompleted.Task.ConfigureAwait(false);
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
