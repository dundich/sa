namespace Sa.Utils.WorkQueue;

using Microsoft.Extensions.Logging;
using System.Threading.Channels;

/// <summary>
/// A bounded work queue. Producers push items into a fixed-size channel, a
/// resizable pool of reader tasks takes them out and runs them, and every item
/// the channel accepted is reported exactly once with a terminal
/// <see cref="SaWorkStatus"/>.
/// </summary>
/// <remarks>
/// <para><b>Two locks, and only two.</b> They are never held at the same time,
/// and never re-entered.</para>
/// <list type="bullet">
/// <item><description><c>_readersSync</c> — the reader pool: the registry, the
/// concurrency limit, and each reader's stop reason. Used by
/// <see cref="SetConcurrencyLimit"/>, <see cref="RegisterReaderUnderLock"/>,
/// <see cref="CancelReadersUnderLock"/>, <see cref="CancelAndTrackReaders"/>,
/// <see cref="RemoveReader"/>, <see cref="LiveReaderCount"/> and
/// <see cref="SnapshotReaderTasks"/>.</description></item>
/// <item><description><c>_pendingSync</c> — the pending-item count and the idle
/// signal. Written only by <see cref="MarkActive"/>,
/// <see cref="MarkInactive"/> and <see cref="ResetPendingIfAllReadersDone"/>.</description></item>
/// </list>
/// <para><b>Slot accounting.</b> A reader holds one slot of the concurrency limit
/// from the moment it is registered until <see cref="RemoveReader"/> takes it
/// out. Handing a slot back early is a separate and explicit step: whoever
/// decides to stop a reader records <see cref="Reader.Stop"/> and releases the
/// slot in the same critical section, so a reader that is already on its way out
/// can never take a second slot's worth of accounting down with it.</para>
/// <para><b>Why a reader's loop is started outside the lock.</b> A loop can run to
/// completion on the thread that started it — the buffer already holds an item and
/// the processor neither yields nor awaits — and that work reports itself through
/// the status callback. Starting loops under <c>_readersSync</c> therefore ran a
/// caller-supplied callback while the pool's lock was held, which deadlocked any
/// handler that waits on a thread needing that lock. So registration and launch are
/// separate steps: <see cref="StartReaders"/> registers under the lock and calls
/// <see cref="LaunchReader"/> after releasing it. Inline execution is kept — it is
/// the queue's normal speed — but it no longer happens with a lock held.</para>
/// </remarks>
public sealed partial class SaWorkQueue<TInput> : ISaWorkQueue<TInput>
{
    /// <summary>One item in the channel: the payload plus the token its producer owns.</summary>
    private sealed record WorkItem(TInput Input, CancellationToken CancellationToken);

    private enum QueueState
    {
        Active = 0,
        Shutdown = 1,
        Disposed = 2
    }

    /// <summary>
    /// Why a reader was told to stop. A reader that is <see cref="None"/> is live
    /// and still holds a slot of the concurrency limit.
    /// </summary>
    private enum ReaderStop
    {
        /// <summary>Live: counts toward the concurrency limit.</summary>
        None = 0,

        /// <summary>
        /// A <see cref="ConcurrencyLimit"/> change picked this reader to make room.
        /// The setter released the slot in the same critical section that set
        /// <see cref="ReaderStop.LimitDecrease"/>, so the reader's own teardown
        /// must not release it a second time.
        /// </summary>
        LimitDecrease = 1,

        /// <summary>
        /// <c>ForceCancelReaders</c> picked this reader. Released eagerly for the
        /// same reason: a limit the caller sets while these readers are still dying
        /// must not be eaten by their teardown.
        /// </summary>
        ForceCancel = 2,
    }

    /// <summary>
    /// One reader task together with the two token sources that control it.
    /// The registry holds a reader from the moment it is created until its loop's
    /// <c>finally</c> calls <see cref="RemoveReader"/> — including the window in
    /// between, where the reader has been cancelled but has not finished yet.
    /// </summary>
    /// <remarks>
    /// Keeping the loop token, the work token, the task and the stop reason on one
    /// object is what makes the pool hard to get wrong: there is no way to reach a
    /// reader's task without also seeing its tokens, and no second list that could
    /// drift out of step with the first. The task is the <see cref="Completion"/>
    /// source, so it exists from construction — a reader is never registered with a
    /// task still to be assigned, which is the window the old
    /// <c>Task?</c> forced every observer to tolerate.
    /// </remarks>
    private sealed class Reader
    {
        public Reader(CancellationTokenSource loop, CancellationTokenSource work)
        {
            Loop = loop;
            Work = work;
        }

        /// <summary>
        /// Cancelling this ends the reader loop. Under
        /// <see cref="SaReaderCancelMode.Soft"/> it deliberately leaves the
        /// in-flight item running.
        /// </summary>
        public CancellationTokenSource Loop { get; }

        /// <summary>
        /// Cancelling this interrupts the in-flight item. Only
        /// <c>ForceCancelReaders</c> uses it, to escalate a soft cancellation to a
        /// hard one.
        /// </summary>
        public CancellationTokenSource Work { get; }

        /// <summary>
        /// Set once, by whoever decided to stop this reader, and only to a value
        /// other than <see cref="ReaderStop.None"/>.
        /// </summary>
        public ReaderStop Stop { get; set; }

        /// <summary>
        /// Completed by <see cref="LaunchReader"/> when the loop ends. Exists from
        /// construction, so <see cref="Task"/> is never <see langword="null"/> and no
        /// observer has to ask whether a reader is the one that has not started yet.
        /// </summary>
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// What to await for this reader. Completes when the loop does, and never
        /// carries its outcome: the loop catches everything itself, so a fault here
        /// would only ever be a bug in the catch.
        /// </summary>
        public Task Task => Completion.Task;

        /// <summary>Whether this reader still holds a slot of the concurrency limit.</summary>
        public bool IsLive => Stop == ReaderStop.None;

        /// <summary>
        /// Whether the loop is known to have finished. A registered-but-unlaunched
        /// reader counts as not finished: it holds a slot and is about to be handed
        /// work, so treating it as done would clear a count that is not clear.
        /// </summary>
        public bool IsFinished => Completion.Task.IsCompleted;

        /// <summary>Releases both token sources. Call once per reader.</summary>
        public void Release()
        {
            Loop.Dispose();
            Work.Dispose();
        }
    }

    private readonly Channel<WorkItem> _queue;

    private readonly Lock _readersSync = new();
    private readonly Lock _pendingSync = new();

    private readonly ILogger? _logger;
    private readonly ISaWork<TInput> _processor;
    private readonly Action<TInput, SaWorkStatus, Exception?>? _statusChanged;
    private readonly Func<TInput, Exception, SaExecutionErrorStrategy> _handleItemFaulted;
    private readonly Func<TInput, string> _getItemDisplayName;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;
    private readonly SaReaderCancellationOrder _cancellationOrder;
    private readonly SaReaderCancelMode _cancelMode;
    private readonly SaEnqueueStrategy _enqueueStrategy;
    private readonly TimeSpan _shutdownTimeout;

    /// <summary>
    /// Clock behind every bounded wait. Injectable so a test can expire a
    /// 30-second timeout instantly instead of sleeping through it — see
    /// <see cref="SaWorkQueueOptions{TInput}.WithTimeProvider"/>.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Where the next <see cref="SaReaderCancellationOrder.RoundRobin"/> sweep continues.</summary>
    private int _lastRemovedIndex = -1;

    /// <summary>The requested concurrency limit. Guarded by <c>_readersSync</c>.</summary>
    private volatile int _concurrency;

    /// <summary>
    /// True only while the limit is explicitly zero. It separates an intentional
    /// pause from an emergency loss of the whole pool, where the effective limit
    /// also drops to zero but nobody asked for it — <c>WaitForIdleAsync</c> has to
    /// tell "nobody will ever drain this" from "the pool is temporarily gone".
    /// Guarded by <c>_readersSync</c>.
    /// </summary>
    private volatile bool _paused;

    /// <summary>The exception that triggered the shutdown, if any.</summary>
    private volatile Exception? _shutdownError;

    /// <summary>
    /// Accepted-but-not-yet-finished items. Written only under
    /// <c>_pendingSync</c>, read without the lock through
    /// <see cref="Volatile.Read(ref int)"/> so that <see cref="QueueTasks"/> and
    /// <see cref="IsIdle"/> stay callable from any thread without taking one.
    /// </summary>
    private int _taskCount;

    /// <summary>
    /// Completed when <see cref="_taskCount"/> reaches zero. Replaced by
    /// <see cref="MarkActive"/> as the count goes back up, so a waiter that is
    /// still parked cannot miss the next idle transition.
    /// </summary>
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Underlying int of <see cref="QueueState"/>. Written only through
    /// Interlocked, read only through <see cref="Volatile.Read(ref int)"/>, so the
    /// state is observed consistently across threads — a plain field read is not
    /// volatile on non-x86 architectures.
    /// </summary>
    private int _state;

    /// <summary>
    /// Every reader that is registered but not yet reaped. Guarded by
    /// <c>_readersSync</c>.
    /// </summary>
    private readonly List<Reader> _readers = [];

    /// <summary>
    /// Single-flight dispose: 0 = not started, 1 = in progress. The winner runs
    /// the shutdown and releases <c>_shutdownCts</c>; a concurrent
    /// <see cref="Dispose()"/>/<see cref="DisposeAsync"/> loser waits for the
    /// winner instead of racing it, which would otherwise dispose the token source
    /// out from under a still-running shutdown.
    /// </summary>
    private int _disposeStarted;

    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SaWorkQueue(SaWorkQueueOptions<TInput> options, ILogger<SaWorkQueue<TInput>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Processor);

        // The options record has a public primary constructor, so the guards of the
        // With* builders can be bypassed entirely. Validate once here, before the
        // channel is created, so an invalid value fails the same way regardless of
        // how the options were assembled.
        if (options.ConcurrencyLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ConcurrencyLimit), options.ConcurrencyLimit, "Concurrency limit must be 0 (paused) or positive.");
        }

        if (options.QueueCapacity is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.QueueCapacity), options.QueueCapacity, "Queue capacity must be at least 1.");
        }

        // "Unbounded" is expressed as null, or as 0 through WithMaxConcurrency,
        // which folds it to the processor count. A negative value has no reading
        // and used to be silently folded to the processor count here as well, so a
        // sign mistake looked like a deliberate default.
        if (options.MaxConcurrency is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxConcurrency), options.MaxConcurrency,
                $"Max concurrency must be 0 (processor count) or positive; use null for the default.");
        }

        if (options.ShutdownTimeout is { } shutdownTimeout && shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ShutdownTimeout), shutdownTimeout, "Shutdown timeout must be positive.");
        }

        _logger = logger;
        _processor = options.Processor;
        _statusChanged = options.StatusChanged;
        _cancellationOrder = options.ReaderCancellationOrder;
        _cancelMode = options.ReaderCancelMode;
        _enqueueStrategy = options.EnqueueStrategy;
        _getItemDisplayName = options.GetItemDisplayName ?? (item => $"{item}");
        _handleItemFaulted = options.HandleItemFaulted ?? ((_, _) => SaExecutionErrorStrategy.ShutdownQueue);
        _shutdownTimeout = options.ShutdownTimeout ?? TimeSpan.FromSeconds(30);
        _timeProvider = options.TimeProvider ?? TimeProvider.System;

        _maxConcurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency.Value : Environment.ProcessorCount;
        _concurrency = Math.Clamp(options.ConcurrencyLimit ?? Environment.ProcessorCount, 0, _maxConcurrency);
        _paused = _concurrency == 0;
        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;

        // AllowSynchronousContinuations = false is what lets a reader run to
        // completion on the thread that hands it an item, instead of handing that
        // thread's stack to the producer.
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = BoundedChannelFullMode.Wait
        });

        StartReaders(_concurrency);
    }

    public bool IsEnabled => Volatile.Read(ref _state) == (int)QueueState.Active;

    public int QueueTasks => Volatile.Read(ref _taskCount);

    public bool IsIdle() => Volatile.Read(ref _taskCount) == 0;

    /// <summary>
    /// The condition of the reader pool, which <see cref="ConcurrencyLimit"/> alone
    /// cannot report.
    /// </summary>
    /// <remarks>
    /// A force-cancel releases the slots of the readers it stops, so the limit drops to
    /// <c>0</c> — the same number a deliberate pause leaves behind, and the live reader
    /// count is <c>0</c> in both cases too. The two states are the ones worth telling
    /// apart and the two the published numbers are unable to.
    /// <para>
    /// Stopped is checked before paused on purpose. A queue paused and then shut down has
    /// no pool left to report on, and answering <see cref="SaWorkPoolState.Paused"/>
    /// would describe an intention rather than the reason nothing is happening.
    /// </para>
    /// <para>
    /// Paused is checked before the live count for the opposite reason: with a limit of
    /// <c>0</c> there are no readers by definition, and "someone set the limit to zero"
    /// is the true answer while "the pool lost its readers" is a guess. The order here is
    /// the one <c>WaitForIdleAsync</c> uses, deliberately, for the same reason.
    /// </para>
    /// </remarks>
    public SaWorkPoolState PoolState
    {
        get
        {
            if (!IsEnabled) return SaWorkPoolState.Stopped;

            lock (_readersSync)
            {
                if (_paused) return SaWorkPoolState.Paused;

                return LiveReaderCount > 0 ? SaWorkPoolState.Active : SaWorkPoolState.NoReaders;
            }
        }
    }

    public int MaxConcurrency => _maxConcurrency;
    public int QueueCapacity => _queueCapacity;

    public int AvailableCapacity
        => _queueCapacity - (_queue.Reader.CanCount ? _queue.Reader.Count : 0);

    public Exception? ShutdownError => _shutdownError;

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            // A negative limit is rejected, exactly as the constructor rejects one
            // and as WithConcurrencyLimit does. Clamping it to zero instead would
            // turn a sign mistake into a queue that stays paused forever, while
            // still reporting IsEnabled == true — a failure with no symptom.
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            SetConcurrencyLimit(Math.Min(value, _maxConcurrency));
        }
    }

    /// <summary>
    /// Applies a new concurrency limit and resizes the pool to match. Called with
    /// a non-negative value already clamped to <see cref="_maxConcurrency"/>.
    /// </summary>
    /// <remarks>
    /// The limit change, the reader bookkeeping and the cancel decisions are one
    /// atomic step. A reader that dies in the middle of it must not be able to
    /// consume the slot that was just created for it, which is why the whole
    /// adjustment happens under a single hold of <c>_readersSync</c>.
    /// <para>
    /// Spawning is the one thing that leaves the lock. The decisions stay inside it
    /// — <see cref="LiveReaderCount"/> is read there, so two concurrent limit changes
    /// cannot both decide to create the same slot — but the loops themselves start
    /// through <see cref="StartReaders"/> afterwards. They are launched rather than
    /// started, so a reader registered by an earlier caller counts towards the limit
    /// for the whole window.
    /// </para>
    /// </remarks>
    private void SetConcurrencyLimit(int newLimit)
    {
        var toStart = 0;

        lock (_readersSync)
        {
            // A stopped queue keeps the limit it was stopped at, and this assignment
            // changes nothing. Writing it anyway would let a caller read back a value
            // the pool does not have: assign 4 after shutdown, read 4, and believe
            // four readers are coming. None are. A caller that only writes and reads
            // has no way to notice, and IsEnabled is not something a shared
            // configuration path checks before resizing a pool it may not own.
            //
            // Silently, rather than by throwing: the assignment is a statement of
            // intent, not an operation on a resource the caller believes it holds.
            // IsEnabled is the answer to "is there a pool to resize" — a caller that
            // needs one to ask.
            if (!IsEnabled) return;

            // Assigning the limit is also the eager release of the slots the readers
            // cancelled below are about to give up — the reason their own teardown
            // must not decrement again.
            _concurrency = newLimit;
            _paused = newLimit == 0;

            // Delta against the readers that actually hold a slot, not against the
            // configured limit: a reader that has been cancelled but has not
            // finished yet is on its way out, and must not trigger a replacement.
            var delta = newLimit - LiveReaderCount;

            if (delta > 0)
            {
                toStart = delta;
            }
            else if (delta < 0)
            {
                CancelReadersUnderLock(-delta);
            }
        }

        StartReaders(toStart);
    }

    /// <summary>
    /// The guard every public entry point shares: a disposed queue reports
    /// <see cref="ObjectDisposedException"/>, a stopped one
    /// <see cref="InvalidOperationException"/>. Both states are terminal and the
    /// check is identical in every case, so it lives in one place.
    /// </summary>
    private void ThrowIfNotActive()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == (int)QueueState.Disposed, this);
        if (!IsEnabled) ThrowHelper.QueueStopped();
    }

    /// <summary>
    /// Runs one item and reports its terminal status. Returns whether the reader
    /// should keep going: <see langword="false"/> means the reader is done
    /// (<see cref="SaExecutionErrorStrategy.StopReader"/>, or a cancellation that
    /// takes the queue down with it).
    /// </summary>
    private async Task<bool> ExecuteItemAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            OnStatusChanged(item.Input, SaWorkStatus.Running);
            await _processor.Execute(item.Input, ct).ConfigureAwait(false);
            OnStatusChanged(item.Input, SaWorkStatus.Completed);
            return true;
        }
        catch (OperationCanceledException ex) when (item.CancellationToken.IsCancellationRequested)
        {
            // The producer gave up on this item: the reader is fine, only the work
            // is over, so the reader stays in the pool. The exception travels with
            // the status — a callback has no logger of its own, so this is the only
            // place it can learn why the item stopped.
            OnStatusChanged(item.Input, SaWorkStatus.Aborted, ex);
            SaWorkQueueLogMessages.LogItemAborted(_logger, _getItemDisplayName(item.Input), ex);
            return true;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // The queue, not the producer, cancelled the work: the reader is going
            // down too.
            OnStatusChanged(item.Input, SaWorkStatus.Cancelled, ex);
            SaWorkQueueLogMessages.LogItemCancelled(_logger, _getItemDisplayName(item.Input), ex);
            return false;
        }
        catch (Exception ex)
        {
            var displayItem = _getItemDisplayName(item.Input);

            SaExecutionErrorStrategy errorStrategy = SaExecutionErrorStrategy.ShutdownQueue;
            try
            {
                OnStatusChanged(item.Input, SaWorkStatus.Faulted, ex);
                SaWorkQueueLogMessages.LogItemExecutionFailed(_logger, displayItem, ex);

                errorStrategy = _handleItemFaulted(item.Input, ex);
            }
            catch (Exception callbackEx)
            {
                // A throwing error handler must not decide the reader's fate; the
                // conservative default (stop the queue) stays in place.
                SaWorkQueueLogMessages.LogItemHandlerFailed(_logger, displayItem, callbackEx);
            }

            return errorStrategy switch
            {
                SaExecutionErrorStrategy.Continue => true,
                SaExecutionErrorStrategy.StopReader => false,
                SaExecutionErrorStrategy.ShutdownQueue => HandleShutdownOnError(ex),
                _ => true
            };
        }
        finally
        {
            MarkInactive();
        }
    }

    private bool HandleShutdownOnError(Exception ex)
    {
        // Keep the cause that started it. ShutdownAsync can return immediately when
        // a concurrent call has already moved the state, so a later fault must not
        // overwrite the original — that one is the reason the queue is down.
        _shutdownError ??= ex;
        _ = ShutdownAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Records an accepted item. The first one replaces the idle signal, so a
    /// caller already parked in <c>WaitForIdleAsync</c> cannot miss the transition.
    /// </summary>
    private void MarkActive()
    {
        TaskCompletionSource? previous = null;

        lock (_pendingSync)
        {
            if (_taskCount++ == 0)
            {
                previous = _idleTcs;
                _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        previous?.TrySetResult();
    }

    /// <summary>
    /// Records a finished or rejected item, releasing the idle signal when the last
    /// one leaves. Never goes below zero: the count is raised before an item is
    /// known to be accepted, and a rejected item takes it straight back down.
    /// </summary>
    private void MarkInactive()
    {
        TaskCompletionSource? idle = null;

        lock (_pendingSync)
        {
            if (_taskCount > 0 && --_taskCount == 0)
            {
                idle = _idleTcs;
            }
        }

        idle?.TrySetResult();
    }

    /// <summary>
    /// Accepted work that has not reached a terminal status yet. Read under the same
    /// lock that guards <c>_taskCount</c> — it is not a volatile field, so a bare read
    /// would be a race.
    /// </summary>
    private int PendingCount
    {
        get
        {
            lock (_pendingSync) return _taskCount;
        }
    }

    /// <summary>
    /// Invokes the status callback and swallows anything it throws — a broken
    /// observer must not take down the queue.
    /// </summary>
    private void OnStatusChanged(TInput item, SaWorkStatus status, Exception? error = null)
    {
        var callback = _statusChanged;
        if (callback is null) return;

        try
        {
            // No queue lock is held: a slow observer must not block other readers,
            // and it may be invoked concurrently by different readers. Reader loops
            // are launched after _readersSync is released, so the callback cannot
            // deadlock against a thread resizing the pool either.
            //
            // The one thing still true, and the remaining constraint: a reader that
            // finds work waiting runs it on this thread, so setting ConcurrencyLimit
            // over a non-empty buffer calls the handler inline, from inside the
            // setter. A handler that waits for that same reader to finish is waiting
            // on itself. Waiting on any *other* thread is fine. Keep the handler
            // self-contained: log, and return.
            callback(item, status, error);
        }
        catch (Exception ex)
        {
            SaWorkQueueLogMessages.LogItemHandlerFailed(_logger, _getItemDisplayName(item), ex);
        }
    }
}
