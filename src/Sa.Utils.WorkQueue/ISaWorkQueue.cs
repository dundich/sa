namespace Sa.Utils.WorkQueue;

/// <summary>
/// Asynchronous task queue with limited parallelism, built on <see cref="System.Threading.Channels.Channel{T}"/>.
/// Provides back-pressure, dynamic concurrency scaling, and per-item error strategies.
/// </summary>
/// <typeparam name="TInput">The type of work item processed by the queue.</typeparam>
public interface ISaWorkQueue<TInput> : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets whether the queue is active and accepting new items.
    /// Returns <see langword="false" /> after shutdown or disposal.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the total number of tasks currently in the queue (queued + actively being processed).
    /// </summary>
    int QueueTasks { get; }

    /// <summary>
    /// Returns <see langword="true" /> if there are no pending or actively processing tasks.
    /// Based on an internal counter, not the channel count, to avoid race conditions.
    /// </summary>
    bool IsIdle();

    /// <summary>
    /// Gets the condition of the reader pool: <see cref="SaWorkPoolState.Active"/>,
    /// <see cref="SaWorkPoolState.Paused"/>, <see cref="SaWorkPoolState.NoReaders"/> or
    /// <see cref="SaWorkPoolState.Stopped"/>.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="IsIdle"/>, and the answer to a question
    /// <see cref="IsIdle"/> provokes: work is pending, so why is nothing happening to
    /// it? Without this, a caller can see that it is not idle and has no way to find out
    /// whether the queue was paused by hand, lost every reader to a
    /// <see cref="ForceCancelReaders"/> or a fault, or has been stopped. Only the third
    /// is a fault, and only the third has an obvious response.
    /// <para>
    /// <see cref="ConcurrencyLimit"/> does not answer it: a force-cancel releases the
    /// slots of the readers it stops, so the limit reads <c>0</c> afterwards exactly as
    /// it does for a pause, and the pool has no readers in either case.
    /// </para>
    /// <para>
    /// One snapshot, not a value that stays true: re-arming the pool or pausing a
    /// running one changes it on the next call. It is meant for a log line, a metric or
    /// a health check, all of which read it once and act.
    /// </para>
    /// </remarks>
    SaWorkPoolState PoolState { get; }

    /// <summary>
    /// Gets or sets the current number of concurrent reader tasks.
    /// Increasing the value spawns new readers; decreasing cancels excess readers
    /// (selected by the configured <see cref="SaReaderCancellationOrder"/>).
    /// Set to <c>0</c> to pause all processing.
    /// </summary>
    /// <remarks>
    /// While the limit is <c>0</c> and items are still queued, <see cref="WaitForIdleAsync"/>
    /// skips the wait and returns immediately: with no readers the queue can never
    /// drain on its own. Raise the limit above <c>0</c> (or shut the queue down) to make progress.
    /// When the queue is created with <see cref="SaReaderCancelMode.Soft"/>, a removed reader
    /// finishes its current item before exiting (<see cref="SaWorkStatus.Completed"/>); in the
    /// default <see cref="SaReaderCancelMode.Hard"/> mode the in-flight item is interrupted
    /// (<see cref="SaWorkStatus.Cancelled"/>) and dropped.
    /// Values outside <c>[0, MaxConcurrency]</c> are clamped to that range.
    /// A negative value is rejected with <see cref="ArgumentOutOfRangeException"/> rather
    /// than clamped, because a queue paused forever while still reporting
    /// <see cref="IsEnabled"/> gives the caller nothing to react to.
    /// <para>
    /// Once the queue is stopped or disposed, the setter does nothing and the getter keeps
    /// reporting the limit the queue was stopped at. Writing it anyway would let a caller
    /// assign a value and read it straight back, concluding that many readers are on their
    /// way when none are. No exception is thrown: the assignment states an intent, and
    /// <see cref="IsEnabled"/> answers whether there is a pool left to resize.
    /// </para>
    /// <para>
    /// This value says how many readers the pool is meant to have, not whether it has
    /// them. <see cref="PoolState"/> is the difference between a deliberate pause and a
    /// pool that lost its readers — which both leave this at <c>0</c>.
    /// </para>
    /// </remarks>
    int ConcurrencyLimit { get; set; }

    /// <summary>
    /// Gets the absolute maximum number of concurrent readers.
    /// <see cref="ConcurrencyLimit"/> cannot exceed this value.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Gets the capacity of the bounded channel.
    /// When the queue is full, <see cref="Enqueue"/> behaves according to the configured
    /// <see cref="SaEnqueueStrategy"/>.
    /// </summary>
    int QueueCapacity { get; }

    /// <summary>
    /// Gets the number of free slots in the bounded channel buffer.
    /// </summary>
    /// <remarks>
    /// Informational value: how many items can be accepted before the buffer is full
    /// and the configured <see cref="SaEnqueueStrategy"/> kicks in.
    /// Items already picked up by readers are not counted;
    /// see <see cref="QueueTasks"/> for queued plus in-flight work.
    /// </remarks>
    int AvailableCapacity { get; }

    /// <summary>
    /// Gets the exception that triggered an automatic shutdown (via <see cref="SaExecutionErrorStrategy.ShutdownQueue"/>),
    /// or <see langword="null" /> if the queue was not faulted.
    /// </summary>
    Exception? ShutdownError { get; }

    /// <summary>
    /// Adds a work item to the queue.
    /// When the buffer is full, the call behaves according to the configured
    /// <see cref="SaEnqueueStrategy"/>:
    /// <see cref="SaEnqueueStrategy.Wait"/> (default) blocks until space is available,
    /// <see cref="SaEnqueueStrategy.Skip"/> drops the item,
    /// <see cref="SaEnqueueStrategy.Throw"/> throws <see cref="SaWorkQueueFullException"/>.
    /// </summary>
    /// <param name="input">The work item to process.</param>
    /// <param name="cancellationToken">
    /// Token for caller-initiated cancellation. If this token is cancelled while the item is being processed,
    /// the item is reported as <see cref="SaWorkStatus.Aborted"/> and the reader continues to the next item.
    /// </param>
    /// <returns>
    /// <see langword="true" /> if the item was accepted into the buffer;
    /// <see langword="false" /> only when the queue is configured with <see cref="SaEnqueueStrategy.Skip"/>
    /// and the buffer is full (the item is dropped and reported as <see cref="SaWorkStatus.Skipped"/>
    /// via the status callback).
    /// </returns>
    /// <exception cref="SaWorkQueueFullException">If the buffer is full and the strategy is <see cref="SaEnqueueStrategy.Throw"/>.</exception>
    /// <exception cref="InvalidOperationException">If the queue has been shut down.</exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled while waiting for buffer space (<see cref="SaEnqueueStrategy.Wait"/> only).</exception>
    ValueTask<bool> Enqueue(TInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to add a work item to the queue without blocking.
    /// Unlike <see cref="Enqueue(TInput, CancellationToken)"/>, this method never honors
    /// the configured <see cref="SaEnqueueStrategy"/>: it performs a single non-blocking
    /// try-write and returns <see langword="false" /> when the buffer is full.
    /// </summary>
    /// <param name="input">The work item to process.</param>
    /// <returns>
    /// <see langword="true" /> if the item was accepted into the buffer;
    /// <see langword="false" /> if the buffer is full (the item is dropped and reported as
    /// <see cref="SaWorkStatus.Skipped"/> via the status callback).
    /// </returns>
    /// <exception cref="InvalidOperationException">If the queue has been shut down.</exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    /// <remarks>
    /// Intended for hot paths where the caller cannot block or await:
    /// it offers the same "drop when full" outcome as <see cref="SaEnqueueStrategy.Skip"/>
    /// regardless of the configured strategy, without any asynchronous machinery.
    /// </remarks>
    bool TryEnqueue(TInput input);

    /// <summary>
    /// Adds multiple work items to the queue in a single call, returning the number of items accepted.
    /// When the bounded buffer is full, each item behaves according to the configured
    /// <see cref="SaEnqueueStrategy"/>:
    /// <see cref="SaEnqueueStrategy.Wait"/> (default) blocks until space is available, so all items
    /// are eventually accepted;
    /// <see cref="SaEnqueueStrategy.Skip"/> drops the items that no longer fit and reports each of them
    /// as <see cref="SaWorkStatus.Skipped"/> via the status callback;
    /// <see cref="SaEnqueueStrategy.Throw"/> throws <see cref="SaWorkQueueFullException"/> as soon as
    /// the buffer becomes full, after the preceding items have been accepted.
    /// </summary>
    /// <param name="inputs">The work items to process, in the order they are enqueued.</param>
    /// <param name="cancellationToken">
    /// Token for caller-initiated cancellation. All enqueued items share this token, so cancelling it
    /// aborts every not-yet-completed item from this batch (<see cref="SaWorkStatus.Aborted"/>).
    /// </param>
    /// <returns>
    /// The number of items accepted into the buffer
    /// (equal to the number of enumerated items unless some were dropped by <see cref="SaEnqueueStrategy.Skip"/>
    /// or the call terminated early).
    /// </returns>
    /// <exception cref="SaWorkQueueFullException">If the buffer is full and the strategy is <see cref="SaEnqueueStrategy.Throw"/>
    /// (carries <see cref="SaWorkQueueFullException.AcceptedCount"/> and <see cref="SaWorkQueueFullException.TotalCount"/>).</exception>
    /// <exception cref="InvalidOperationException">If the queue has been shut down.</exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled while waiting for space.</exception>
    ValueTask<int> EnqueueMany(IEnumerable<TInput> inputs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously waits until all currently queued and in-progress tasks have completed.
    /// Returns immediately if the queue is already idle, or if it cannot reach idle on its own
    /// (see <paramref name="failIfNoProgress"/>).
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait. The wait can be resumed by calling again.</param>
    /// <param name="failIfNoProgress">
    /// When <see langword="true" />, pending work that no reader can ever reach throws
    /// <see cref="InvalidOperationException"/> instead of returning. Leave it <see langword="false" />
    /// to read that state as "not idle, and never will be" — the honest answer to "is it idle?",
    /// and usually what a caller wants.
    /// </param>
    /// <remarks>
    /// Two states cannot drain on their own. They share one flag because they mean the same
    /// thing to a caller waiting here:
    /// <list type="bullet">
    /// <item><description>the queue is <em>paused</em> (<see cref="ConcurrencyLimit"/> was set to
    /// <c>0</c>) with items still queued;</description></item>
    /// <item><description>the queue is active but has <em>no live readers</em> — every reader was
    /// lost to <see cref="ForceCancelReaders"/> or a
    /// <see cref="SaExecutionErrorStrategy.StopReader"/> fault, and nothing re-armed the pool. The
    /// wait returns here too, because an empty pool cannot make progress. Set
    /// <see cref="ConcurrencyLimit"/> above 0 to spawn replacements.</description></item>
    /// </list>
    /// An already-idle queue returns immediately regardless of the flag.
    /// <para>
    /// Either non-idle return is logged as a warning. Together with
    /// <see cref="IsIdle"/> answering <see langword="false"/> for the same state, that is what
    /// makes the two agree: work is genuinely still pending, no reader is coming for it, and a
    /// caller waiting here is told so rather than handed a silent no-op.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="InvalidOperationException">
    /// If items are pending that no live reader can process, and <paramref name="failIfNoProgress"/>
    /// is <see langword="true" />.
    /// </exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    Task WaitForIdleAsync(bool failIfNoProgress = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the queue: cancels all readers (in-flight work is interrupted, not finished),
    /// completes the channel, waits for the readers to exit (bounded by the shutdown timeout),
    /// and drains the remaining buffer items (reporting them as <see cref="SaWorkStatus.Faulted"/>,
    /// or <see cref="SaWorkStatus.Aborted"/> when the caller's token was already cancelled).
    /// Subsequent calls to <see cref="Enqueue"/> will throw <see cref="InvalidOperationException"/>.
    /// This method is idempotent.
    /// </summary>
    /// <remarks>
    /// Never call this from inside <see cref="ISaWork{TInput}.Execute"/>: the calling reader is
    /// one of the tasks being awaited, so the call blocks for the full
    /// <see cref="SaWorkQueueOptions{TInput}.ShutdownTimeout"/> (30 s by default) and only then
    /// returns. Hand the work off to a background task instead (see <c>JobScheduler.AbortJob</c>).
    /// </remarks>
    Task ShutdownAsync();

    /// <summary>
    /// Synchronously shuts down the queue. Behaves identically to <see cref="ShutdownAsync"/>
    /// but blocks the calling thread while waiting for the readers to exit
    /// (bounded by the shutdown timeout, default 30 s).
    /// This method is idempotent.
    /// </summary>
    /// <remarks>
    /// Never call this from inside <see cref="ISaWork{TInput}.Execute"/> — see
    /// <see cref="ShutdownAsync"/> for why, and use a background task instead.
    /// </remarks>
    void Shutdown();

    /// <summary>
    /// Emergency stop: immediately cancels all reader tasks and waits for them to terminate
    /// (bounded by the shutdown timeout, default 30 s).
    /// The queue remains active — set <see cref="ConcurrencyLimit"/> to a positive value to spawn replacement readers.
    /// This method is idempotent.
    /// </summary>
    /// <remarks>
    /// Always interrupts in-flight work (<see cref="SaWorkStatus.Cancelled"/>) even when the queue
    /// is configured with <see cref="SaReaderCancelMode.Soft"/>.
    /// Never call this from inside <see cref="ISaWork{TInput}.Execute"/>: the calling reader is
    /// one of the tasks being awaited, so the call blocks for the full
    /// <see cref="SaWorkQueueOptions{TInput}.ShutdownTimeout"/>. Use a background task instead.
    /// </remarks>
    void ForceCancelReaders();

    /// <summary>
    /// Async emergency stop: immediately cancels all reader tasks and awaits their termination.
    /// The queue remains active — set <see cref="ConcurrencyLimit"/> to a positive value to spawn replacement readers.
    /// This method is idempotent.
    /// </summary>
    /// <param name="timeout">Optional maximum time to wait for readers to terminate. If <see langword="null"/>, waits indefinitely.</param>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="TimeoutException">If <paramref name="timeout"/> elapses before all readers terminate.</exception>
    /// <remarks>
    /// Always interrupts in-flight work (<see cref="SaWorkStatus.Cancelled"/>) even when the queue
    /// is configured with <see cref="SaReaderCancelMode.Soft"/>.
    /// Never <c>await</c> this from inside <see cref="ISaWork{TInput}.Execute"/> without first
    /// hopping to a background task: the calling reader is one of the tasks being awaited,
    /// so the await can never complete on its own.
    /// <para>
    /// Unlike the synchronous <see cref="ForceCancelReaders"/>, this method does <em>not</em> drop
    /// the buffer when the wait ends early. A caller-supplied <paramref name="timeout"/> is a
    /// statement about how long the readers should take, not permission to discard queued work:
    /// on <see cref="TimeoutException"/> or cancellation the items still in the buffer are kept,
    /// no reader is picking them up, and a warning is logged saying so. Raise
    /// <see cref="ConcurrencyLimit"/> above 0 to spawn a replacement pool and let them run.
    /// </para>
    /// <para>
    /// <see cref="IsIdle"/> reports <see langword="false"/> in that state, and keeps doing so until
    /// the buffer is drained or dropped. That is not a contradiction with
    /// <see cref="WaitForIdleAsync"/>, which returns rather than hanging on a queue that can
    /// never drain; the latter logs a warning when it gives up for that reason.
    /// </para>
    /// </remarks>
    Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
