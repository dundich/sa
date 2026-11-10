using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Sa.Utils.WorkQueue;
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
/// <see cref="MarkInactive()"/> and <see cref="ResetPendingIfAllReadersDone"/>.</description></item>
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
public sealed partial class SaWorkChannel<TInput> : ISaWorkChannel<TInput>
{
    /// <summary>One item in the channel: the payload plus the token its producer owns.</summary>
    /// <remarks>
    /// A struct rather than a class: the class added one 32-byte object per enqueue,
    /// which was the dominant per-item allocation on the synchronous path. The
    /// bounded channel preallocates its ring for the whole capacity, so this trades a
    /// fixed <c>capacity × sizeof(WorkItem)</c> buffer for zero per-item objects —
    /// good for reference payloads and small structs. A very large
    /// <typeparamref name="TInput"/> pays for it in the preallocated buffer (the ring
    /// stores the payload inline), which is the one case worth re-measuring.
    /// </remarks>
    private readonly struct WorkItem
    {
        public WorkItem(TInput input, CancellationToken cancellationToken)
        {
            Input = input;
            CancellationToken = cancellationToken;
        }

        public TInput Input { get; }

        public CancellationToken CancellationToken { get; }
    }

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
    /// One reader task together with the token sources that control it.
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
        public Reader(CancellationTokenSource loop, CancellationTokenSource? work)
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
        /// hard one. <see langword="null"/> in <see cref="SaReaderCancelMode.Hard"/>
        /// mode: there the in-flight item already runs under <see cref="Loop"/>, so
        /// a second source would be created, linked and never read.
        /// </summary>
        public CancellationTokenSource? Work { get; }

        /// <summary>
        /// Set once, by whoever decided to stop this reader, and only to a value
        /// other than <see cref="ReaderStop.None"/>.
        /// </summary>
        public ReaderStop Stop { get; set; }

        /// <summary>
        /// Set by <see cref="RemoveReader"/> when this reader leaves the registry, under the
        /// same <c>_readersSync</c> that guards the list.
        /// </summary>
        /// <remarks>
        /// A force-cancel works from a snapshot taken before this flag is set, and must not
        /// release the slot a second time for a reader that died in the meantime. Asking the
        /// registry with <c>Contains</c> answered the same question, but made the check a
        /// linear scan per reader — O(n²) with the pool's lock held on every step.
        /// </remarks>
        public bool Removed { get; set; }

        /// <summary>
        /// Completed by the loop's <c>finally</c> when it ends. Exists from
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
            Work?.Dispose();
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
    private readonly SaProducerCancellationMode _producerCancellation;
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

    /// <summary>
    /// The exception that triggered the shutdown, if any. <c>volatile</c> covers the read
    /// side — <see cref="ShutdownError"/> returns it without a lock. The write side is a
    /// compare-exchange, because two readers can fault at the same instant and the first
    /// cause is the one worth keeping; that call passes the field by <c>ref</c>, which is
    /// the one place CS0420 is suppressed (see <see cref="HandleShutdownOnError"/>).
    /// </summary>
    private volatile Exception? _shutdownError;

    /// <summary>
    /// Accepted-but-not-yet-finished items. Written only under
    /// <c>_pendingSync</c>, read without the lock through
    /// <c>Volatile.Read(ref int)</c> so that <see cref="QueueTasks"/> and
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
    /// Interlocked, read only through <c>Volatile.Read(ref int)</c>, so the
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
    /// the shutdown; a concurrent <see cref="Dispose()"/>/<see cref="DisposeAsync"/> loser
    /// waits for the winner instead of racing it, which would otherwise tear the queue down
    /// while a shutdown was still reporting what it dropped.
    /// </summary>
    private int _disposeStarted;

    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Signalled when the shutdown that won <c>_state</c> has finished: readers gone and
    /// the buffer reported. A caller that arrives while that shutdown is still running
    /// waits for this instead of returning early, so "the queue is shut down" means the
    /// same thing to every caller — the same reason <see cref="_disposeCompleted"/> exists.
    /// </summary>
    private readonly TaskCompletionSource _shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Non-zero while the current logical flow is inside a shutdown body of <em>this</em>
    /// queue. A re-entrant call — a status callback invoked by the drain asking to shut the
    /// queue down again — must not wait for the completion that the flow it interrupts is
    /// going to signal: it would wait on itself, and no timeout covers that wait. Async-local
    /// rather than thread-static because a shutdown body crosses <c>await</c>s, and the
    /// callback that makes the re-entrant call runs on whichever thread continues it.
    /// </summary>
    /// <remarks>
    /// Deliberately an instance field, not <c>static</c>. The guard belongs to one queue: a
    /// static one is shared by everything flowing through the same <c>ExecutionContext</c>, so
    /// code running inside queue A's shutdown that shuts down queue B would skip B's
    /// <c>_shutdownCompleted</c> wait and return while B was still draining — the exact
    /// difference the completion exists to remove.
    /// </remarks>
    private readonly AsyncLocal<int> _shutdownDepth = new();

    /// <summary>Whether the current logical flow is already running a shutdown of this queue.</summary>
    private bool IsShuttingDownHere => _shutdownDepth.Value > 0;

    /// <summary>Creates a queue over the given options and starts its reader pool.</summary>
    /// <param name="options">The validated queue configuration.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly",
        Justification = "The ctor validates an options record; the offending member is the property, and naming it in ParamName is what callers need.")]
    public SaWorkChannel(SaWorkQueueOptions<TInput> options, ILogger<SaWorkChannel<TInput>>? logger = null)
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

        // The three strategy enums are read through switch arms and equality tests, so an
        // undefined value does not fail — it quietly becomes one of the real ones: an
        // unrecognised enqueue strategy behaves as Skip, an unrecognised cancel mode as
        // Hard, an unrecognised cancellation order as Lifo. A typo in configuration then
        // looks like a deliberate choice, which is the same failure the integer checks
        // above exist to prevent, so it is rejected here next to them.
        if (!Enum.IsDefined(options.EnqueueStrategy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.EnqueueStrategy), options.EnqueueStrategy,
                "Enqueue strategy must be Wait, Skip or Throw.");
        }

        if (!Enum.IsDefined(options.ReaderCancelMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ReaderCancelMode), options.ReaderCancelMode,
                "Reader cancel mode must be Hard or Soft.");
        }

        if (!Enum.IsDefined(options.ReaderCancellationOrder))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ReaderCancellationOrder), options.ReaderCancellationOrder,
                "Reader cancellation order must be Lifo, Fifo, RoundRobin or Random.");
        }

        if (!Enum.IsDefined(options.ProducerCancellation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ProducerCancellation), options.ProducerCancellation,
                "Producer cancellation mode must be Observe or Detached.");
        }

        _logger = logger;
        _processor = options.Processor;
        _statusChanged = options.StatusChanged;
        _cancellationOrder = options.ReaderCancellationOrder;
        _cancelMode = options.ReaderCancelMode;
        _enqueueStrategy = options.EnqueueStrategy;
        _producerCancellation = options.ProducerCancellation;
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
        //
        // SingleReader is safe to promise when the pool can never hold more than one
        // reader: ConcurrencyLimit is clamped to _maxConcurrency, so a maximum of 1
        // means no later resize can add a second one. The channel can then use its
        // cheaper single-reader wake path.
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = _maxConcurrency == 1,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = BoundedChannelFullMode.Wait
        });

        StartReaders(_concurrency);
    }

    /// <summary>Whether the queue accepts new work (not paused, stopped or disposed).</summary>
    public bool IsEnabled => Volatile.Read(ref _state) == (int)QueueState.Active;

    /// <summary>Number of accepted items that have not finished yet.</summary>
    public int QueueTasks => Volatile.Read(ref _taskCount);

    /// <summary>Whether no accepted item is still in flight.</summary>
    /// <returns><see langword="true"/> when the queue has nothing left to finish.</returns>
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

    /// <summary>The configured upper bound of the reader pool.</summary>
    public int MaxConcurrency => _maxConcurrency;
    /// <summary>The buffer capacity configured at construction.</summary>
    public int QueueCapacity => _queueCapacity;

    /// <summary>Free slots left in the buffer.</summary>
    public int AvailableCapacity
        => _queueCapacity - (_queue.Reader.CanCount ? _queue.Reader.Count : 0);

    /// <summary>The error that triggered the shutdown, if the queue stopped because of one.</summary>
    public Exception? ShutdownError => _shutdownError;

    /// <summary>The current reader count; setting it resizes the pool (0 pauses the queue).</summary>
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
    /// The limit change, the reader bookkeeping, the cancel decisions and the registration
    /// of the readers being added are one atomic step. A reader that dies in the middle of
    /// it must not be able to consume the slot that was just created for it, and two callers
    /// raising the limit at the same moment must not both decide to create the same slot:
    /// the new readers are registered under the same hold of <c>_readersSync</c> that reads
    /// <see cref="LiveReaderCount"/>, so the second caller sees the slots the first one
    /// already took and computes a delta of zero. Deciding inside the lock and registering
    /// outside it left the exact window in which both computed the same delta.
    /// <para>
    /// Launching is the one thing that leaves the lock. The loops start through
    /// <see cref="LaunchRegistered"/> after it is released, so a reader that finds work
    /// waiting — and therefore runs a caller-supplied status callback on this very thread —
    /// never does so with the pool's lock held.
    /// </para>
    /// </remarks>
    private void SetConcurrencyLimit(int newLimit)
    {
        Reader[]? toStart = null;
        var started = 0;

        try
        {
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
                    toStart = RegisterReadersUnderLock(delta, out started);
                }
                else if (delta < 0)
                {
                    CancelReadersUnderLock(-delta);
                }
            }
        }
        finally
        {
            // Launched even when registration threw partway: a reader that is already in
            // the registry but never launched would hold a slot forever and never complete
            // its published task, stalling every shutdown wait on it.
            if (toStart is not null)
            {
                LaunchRegistered(toStart, started);
            }
        }

        // A pause is the one way the pool stops being able to make progress without any
        // item finishing. The items already accepted stay counted, so a waiter parked in
        // WaitForIdleAsync would never be released by the count alone: wake it to re-read
        // PoolState, which is the answer it exists to give.
        if (_paused)
        {
            WakeStalledIdleWaiter();
        }
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

    private bool HandleShutdownOnError(Exception ex)
    {
        // Keep the cause that started it. ShutdownAsync can return immediately when
        // a concurrent call has already moved the state, so a later fault must not
        // overwrite the original — that one is the reason the queue is down. A
        // compare-exchange, not a read-then-write: two readers can fault at the same
        // moment, and a `??=` would let whichever finished last win rather than the one
        // that got there first.
        //
        // The field is volatile for the lock-free read in ShutdownError, so passing it by
        // ref here is exactly the case CS0420 describes as legitimate — an interlocked API
        // — and the compiler asks only that it be spelled out.
#pragma warning disable CS0420 // a volatile field is intentionally passed by ref to an interlocked API
        Interlocked.CompareExchange(ref _shutdownError, ex, null);
#pragma warning restore CS0420
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
    private void MarkInactive() => MarkInactive(1);

    /// <summary>
    /// Records <paramref name="count"/> finished items under one hold of
    /// <c>_pendingSync</c>. The drain uses it to give back a whole buffer at once
    /// instead of taking the same lock once per dropped item.
    /// </summary>
    private void MarkInactive(int count)
    {
        if (count <= 0) return;

        TaskCompletionSource? idle = null;

        lock (_pendingSync)
        {
            if (_taskCount > 0)
            {
                _taskCount -= Math.Min(count, _taskCount);

                if (_taskCount == 0)
                {
                    idle = _idleTcs;
                }
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
    /// Releases a waiter parked in <see cref="WaitForIdleAsync"/> without claiming the
    /// queue reached idle, so it wakes up and re-reads <see cref="PoolState"/>.
    /// </summary>
    /// <remarks>
    /// Called when the pool stops being able to make progress — a pause, a force-cancel,
    /// the last live reader dying — while items are still accepted. The idle signal alone
    /// can never release that waiter: it fires only when the count falls to zero, and
    /// nothing is going to finish the remaining items. Without this the wait parks forever
    /// on a queue that <see cref="PoolState"/> is already reporting as <see cref="SaWorkPoolState.Paused"/>
    /// or <see cref="SaWorkPoolState.NoReaders"/>, which is exactly the state the wait is
    /// documented to return on.
    /// <para>
    /// The signal is replaced as well as completed. A waiter that wakes and finds the pool
    /// re-armed by a concurrent limit raise must park on a fresh signal; handing it the
    /// completed one would turn the loop into a spin until the count happened to drop.
    /// </para>
    /// </remarks>
    private void WakeStalledIdleWaiter()
    {
        TaskCompletionSource? previous = null;

        lock (_pendingSync)
        {
            if (_taskCount > 0)
            {
                previous = _idleTcs;
                _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        previous?.TrySetResult();
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
            if (_logger is not null)
            {
                SaWorkQueueLogMessages.LogItemHandlerFailed(_logger, _getItemDisplayName(item), ex);
            }
        }
    }
}
