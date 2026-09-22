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
    /// Returns immediately if the queue is already idle, or if it is paused
    /// (<see cref="ConcurrencyLimit"/> is <c>0</c>) with pending items that no reader can process.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the wait. The wait can be resumed by calling again.</param>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled.</exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the queue: cancels all readers (in-flight work is interrupted, not finished),
    /// completes the channel, waits for the readers to exit (bounded by the shutdown timeout),
    /// and drains the remaining buffer items (reporting them as <see cref="SaWorkStatus.Faulted"/>,
    /// or <see cref="SaWorkStatus.Aborted"/> when the caller's token was already cancelled).
    /// Subsequent calls to <see cref="Enqueue"/> will throw <see cref="InvalidOperationException"/>.
    /// This method is idempotent.
    /// </summary>
    Task ShutdownAsync();

    /// <summary>
    /// Synchronously shuts down the queue. Behaves identically to <see cref="ShutdownAsync"/>
    /// but blocks the calling thread while waiting for the readers to exit
    /// (bounded by the shutdown timeout, default 30 s).
    /// This method is idempotent.
    /// </summary>
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
    /// </remarks>
    Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
