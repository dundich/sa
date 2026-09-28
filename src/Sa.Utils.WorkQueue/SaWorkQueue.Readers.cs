namespace Sa.Utils.WorkQueue;

using System.Threading.Channels;

/// <summary>
/// The consuming half of the queue: the reader pool, how it is resized, and how
/// callers observe it becoming idle.
/// </summary>
/// <remarks>
/// A reader is a plain loop — read an item, run it, repeat — and everything that
/// makes it interesting happens around it: it is registered before it starts, it
/// can be told to stop from three directions, and it is only released from its
/// own <c>finally</c>. Each of those is a single method below.
/// </remarks>
public sealed partial class SaWorkQueue<TInput>
{
    /// <summary>
    /// How many readers still hold a slot of the concurrency limit, i.e. how many
    /// were never told to stop. Cancelled-but-unreaped readers do not count:
    /// they are on their way out and must not trigger a replacement.
    /// </summary>
    private int LiveReaderCount
    {
        get
        {
            var live = 0;
            foreach (var reader in _readers)
            {
                if (reader.IsLive) live++;
            }

            return live;
        }
    }

    private bool HasLiveReaders()
    {
        lock (_readersSync)
        {
            return LiveReaderCount > 0;
        }
    }

    /// <summary>
    /// The loop tasks of every tracked reader, for anything that has to wait for
    /// the pool to wind down.
    /// </summary>
    /// <remarks>
    /// Must be called under <c>_readersSync</c> — which is also what guarantees
    /// every <see cref="Reader.Task"/> is already assigned, since a reader is added
    /// to the registry and given its task in the same critical section.
    /// </remarks>
    private Task[] SnapshotReaderTasks()
        => [.. _readers.Select(static r => r.Task).OfType<Task>()];

    /// <summary>Starts <paramref name="count"/> readers. Must be called under <c>_readersSync</c>.</summary>
    private void StartReadersUnderLock(int count)
    {
        for (var i = 0; i < count; i++)
        {
            StartReaderUnderLock();
        }
    }

    /// <summary>
    /// Creates a reader, registers it, and starts its loop. Must be called under
    /// <c>_readersSync</c>, which the reader's own teardown may re-enter.
    /// </summary>
    /// <remarks>
    /// Register <em>before</em> starting. The loop can run to completion inline —
    /// the buffer already holds an item and the processor neither yields nor awaits
    /// — and then <see cref="RemoveReader"/> runs on this very thread, re-entering
    /// the re-entrant lock and taking the reader straight back out. Because the
    /// registry was written first, that case is identical to a reader that dies a
    /// millisecond later, and needs no branch of its own.
    /// <para>
    /// The order matters. A reader registered only after it finished is one the
    /// accounting never saw, yet the caller would then have a permanently disposed
    /// token in the registry: it reports as not cancelled, so it counted as live
    /// forever, which made <c>WaitForIdleAsync</c> wait for an idle that could not
    /// arrive and threw <see cref="ObjectDisposedException"/> out of the
    /// <see cref="ConcurrencyLimit"/> setter.
    /// </para>
    /// </remarks>
    private void StartReaderUnderLock()
    {
        // Both sources are linked to the shutdown token, so shutdown stays a hard
        // stop in every mode.
        var reader = new Reader(
            CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token),
            CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token));

        _readers.Add(reader);
        reader.Task = ReaderLoopAsync(reader);
    }

    /// <summary>
    /// Tells <paramref name="toCancel"/> live readers to stop, in the configured
    /// order. Must be called under <c>_readersSync</c>.
    /// </summary>
    private void CancelReadersUnderLock(int toCancel)
    {
        var live = new List<Reader>();
        foreach (var reader in _readers)
        {
            if (reader.IsLive) live.Add(reader);
        }

        if (live.Count == 0) return;

        toCancel = Math.Min(toCancel, live.Count);

        foreach (var idx in SelectRemovalIndices(live.Count, toCancel))
        {
            var reader = live[idx];

            // Recording the decision and cancelling are one step, under the same
            // hold as the limit change that asked for it. LiveReaderCount stops
            // counting this reader immediately, and the slot is not released again
            // when its loop unwinds.
            reader.Stop = ReaderStop.LimitDecrease;
            reader.Loop.Cancel();
        }
    }

    /// <summary>
    /// Which of <paramref name="total"/> readers to stop, as indices into the
    /// list of live readers.
    /// </summary>
    private int[] SelectRemovalIndices(int total, int toCancel)
        => _cancellationOrder switch
        {
            SaReaderCancellationOrder.Lifo => [.. Enumerable.Range(total - toCancel, toCancel)],
            SaReaderCancellationOrder.Fifo => [.. Enumerable.Range(0, toCancel)],
            SaReaderCancellationOrder.RoundRobin => RoundRobinIndices(total, toCancel),
            SaReaderCancellationOrder.Random => RandomIndices(total, toCancel),
            _ => [.. Enumerable.Range(total - toCancel, toCancel)]
        };

    private int[] RoundRobinIndices(int totalCount, int toCancel)
    {
        var start = (_lastRemovedIndex + 1) % totalCount;
        var indices = new int[toCancel];
        for (var i = 0; i < toCancel; i++)
        {
            indices[i] = (start + i) % totalCount;
        }

        _lastRemovedIndex = (start + toCancel - 1) % totalCount;
        return indices;
    }

    private static int[] RandomIndices(int totalCount, int toCancel)
    {
        // Partial Fisher-Yates: the swap source must be able to address any of
        // the `totalCount` live readers, so the buffer is full-sized and only the
        // first `toCancel` positions are returned. O(totalCount) time and space,
        // with a guaranteed-distinct, uniform result. Rejection sampling (draw
        // until distinct) would degrade toward O(n^2) when toCancel approaches
        // totalCount.
        var buffer = new int[totalCount];
        for (var i = 0; i < totalCount; i++)
        {
            buffer[i] = i;
        }

        var rng = Random.Shared;
        for (var i = 0; i < toCancel; i++)
        {
            var j = i + rng.Next(totalCount - i);
            (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        return buffer[..toCancel];
    }

    /// <summary>
    /// The reader loop: take an item, run it, repeat until cancelled. Every exit
    /// — clean, cancelled, or faulted — goes through <see cref="RemoveReader"/>.
    /// </summary>
    private async Task ReaderLoopAsync(Reader reader)
    {
        try
        {
            while (!reader.Loop.IsCancellationRequested)
            {
                WorkItem item;
                try
                {
                    item = await _queue.Reader.ReadAsync(reader.Loop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                // In Soft mode the item runs under the work token rather than the
                // loop token, so a planned removal or a pause does not interrupt
                // work in flight. The queue itself is still a hard stop in every
                // mode, because the work token is linked to the shutdown token too.
                var workParent = _cancelMode == SaReaderCancelMode.Soft ? reader.Work.Token : reader.Loop.Token;

                // A linked source is only needed when the producer's token can
                // actually fire; a default token cannot be cancelled, so skip the
                // per-item allocation on that common path.
                using var ctsExec = item.CancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(workParent, item.CancellationToken)
                    : null;

                if (!await ExecuteItemAsync(item, ctsExec?.Token ?? workParent).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The shutdown token source was disposed out from under this reader:
            // Dispose ran after the shutdown wait timed out while this reader was
            // still alive. An expected late exit — no extra shutdown, no alarming
            // log. RemoveReader in the finally still cleans up.
        }
        catch (Exception ex)
        {
            SaWorkQueueLogMessages.LogReaderError(_logger, ex);
            _ = ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            RemoveReader(reader);
        }
    }

    /// <summary>
    /// Takes a reader out of the registry and releases its token sources. Runs from
    /// the reader loop's <c>finally</c>, on the reader's own thread — possibly
    /// inline on the thread that started it, re-entering <c>_readersSync</c>.
    /// </summary>
    private void RemoveReader(Reader reader)
    {
        bool lostSlot;

        lock (_readersSync)
        {
            _readers.Remove(reader);

            // An unplanned death — a processor fault under StopReader, or a crash
            // in the loop — really does cost the pool a slot, so the limit drops
            // and recovery is to set ConcurrencyLimit again. A planned death
            // (ReaderStop already set) gave its slot back at the moment the
            // decision was taken; releasing it here as well is what used to let
            // readers on their way out eat a limit the caller had set meanwhile.
            lostSlot = reader.IsLive;
            if (lostSlot && _concurrency > 0)
            {
                _concurrency--;
            }
        }

        reader.Release();

        if (lostSlot && IsEnabled)
        {
            SaWorkQueueLogMessages.LogReaderLost(_logger, _concurrency);
        }
    }

    /// <summary>
    /// Cancels every live reader and records why, so the slots they hold are
    /// released now rather than when their loops finally unwind. Returns the loop
    /// tasks of the tracked readers. Shared by <see cref="ForceCancelReaders"/>
    /// and <see cref="ForceCancelReadersAsync"/>; only callable while the queue
    /// is enabled.
    /// </summary>
    private Task[] CancelAndTrackReaders()
    {
        Task[] tasks;
        Reader[] readers;

        lock (_readersSync)
        {
            readers = [.. _readers];
            tasks = SnapshotReaderTasks();
        }

        foreach (var reader in readers)
        {
            // Deciding and recording in one critical section is what makes a
            // force-cancel idempotent. A reader already stopped by a limit change,
            // or by a concurrent call, is skipped rather than released twice.
            lock (_readersSync)
            {
                if (!reader.IsLive) continue;
                if (reader.Loop.IsCancellationRequested) continue; // shutdown got here first

                reader.Stop = ReaderStop.ForceCancel;

                if (_concurrency > 0)
                {
                    _concurrency--;
                }
            }

            try
            {
                reader.Loop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                continue; // reaped by RemoveReader between the snapshot and here
            }

            // A force-cancel is always hard, even in Soft mode: cancelling the work
            // token interrupts the item in flight instead of letting it finish.
            if (_cancelMode == SaReaderCancelMode.Soft)
            {
                try
                {
                    reader.Work.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // reaped concurrently; nothing left to escalate
                }
            }
        }

        return tasks;
    }

    /// <summary>
    /// Waits until no accepted item is still being worked on.
    /// </summary>
    /// <remarks>
    /// Only a <em>disposed</em> queue is rejected here. A stopped one is a normal
    /// thing to wait on: shutdown drains what is left, and that drain is exactly
    /// what the caller is waiting for.
    /// <para>
    /// Both early-return branches are the same idea — the queue cannot reach idle
    /// on its own, and hanging would be a lie. <see cref="failIfNoProgress"/> picks
    /// whether that is a silent return or an error, for callers that must not
    /// proceed on a queue that will never drain.
    /// </para>
    /// </remarks>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default, bool failIfNoProgress = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == (int)QueueState.Disposed, this);

        while (true)
        {
            TaskCompletionSource idle;
            lock (_pendingSync)
            {
                if (_taskCount == 0) return;
                idle = _idleTcs;
            }

            if (_paused)
            {
                if (failIfNoProgress) ThrowHelper.QueuePaused();
                return;
            }

            // Same dead end, different cause: the queue is not paused but the pool
            // is empty, because every reader was force-cancelled or lost to a
            // fault and nothing re-armed it. Setting ConcurrencyLimit restores it.
            if (IsEnabled && !HasLiveReaders())
            {
                if (failIfNoProgress) ThrowHelper.QueueHasNoReaders();
                return;
            }

            try
            {
                await idle.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // Only the caller's token is a real cancellation here; anything
                // else is the idle signal firing, so loop and re-check.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Idle wait was cancelled by the user.", ex, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// Zeroes the pending count and releases the idle signal, but only once no
    /// reader can still be holding an item. A reader that outlived a timeout is
    /// still working, and zeroing here would claim idle while work is in flight —
    /// its own <see cref="MarkInactive"/> is a guarded no-op by then and could not
    /// restore the count.
    /// </summary>
    private void ResetPendingIfAllReadersDone()
    {
        bool allReadersDone;
        lock (_readersSync)
        {
            allReadersDone = _readers.All(static r => r.IsFinished);
        }

        if (!allReadersDone) return;

        TaskCompletionSource? idle = null;

        lock (_pendingSync)
        {
            if (_taskCount > 0)
            {
                _taskCount = 0;
                idle = _idleTcs;
            }
        }

        idle?.TrySetResult();
    }
}
