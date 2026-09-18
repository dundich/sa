namespace Sa.Utils.WorkQueue;

/// <summary>
/// Asynchronous task queue with limited parallelism, built on <see cref="System.Threading.Channels.Channel{T}"/>.
/// Provides back-pressure, dynamic concurrency scaling, and per-item error strategies.
/// </summary>
/// <typeparam name="TInput">The type of work item processed by the queue.</typeparam>
public interface ISaWorkQueue<in TInput> : IDisposable, IAsyncDisposable
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
    /// (selected by the configured <see cref="SaReaderScalingStrategy"/>).
    /// Set to <c>0</c> to pause all processing.
    /// </summary>
    /// <remarks>
    /// While the limit is <c>0</c> and items are still queued, <see cref="WaitForIdleAsync"/>
    /// skips the wait and returns immediately: with no readers the queue can never
    /// drain on its own. Raise the limit above <c>0</c> (or shut the queue down) to make progress.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">If the value exceeds <see cref="MaxConcurrency"/> (it is clamped).</exception>
    int ConcurrencyLimit { get; set; }

    /// <summary>
    /// Gets the absolute maximum number of concurrent readers.
    /// <see cref="ConcurrencyLimit"/> cannot exceed this value.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Gets the capacity of the bounded channel.
    /// When the queue is full, <see cref="Enqueue"/> blocks until space is available.
    /// </summary>
    int QueueCapacity { get; }

    /// <summary>
    /// Gets the exception that triggered an automatic shutdown (via <see cref="SaExecutionErrorStrategy.ShutdownQueue"/>),
    /// or <see langword="null" /> if the queue was not faulted.
    /// </summary>
    Exception? ShutdownError { get; }

    /// <summary>
    /// Adds a work item to the queue.
    /// If the queue is full, the call blocks until space is available.
    /// </summary>
    /// <param name="input">The work item to process.</param>
    /// <param name="cancellationToken">
    /// Token for caller-initiated cancellation. If this token is cancelled while the item is being processed,
    /// the item is reported as <see cref="SaWorkStatus.Aborted"/> and the reader continues to the next item.
    /// </param>
    /// <exception cref="InvalidOperationException">If the queue has been shut down.</exception>
    /// <exception cref="ObjectDisposedException">If the queue has been disposed.</exception>
    ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default);

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
    /// Gracefully shuts down the queue: cancels all readers, completes the channel,
    /// waits for in-flight items to finish, and drains remaining items (reporting them as <see cref="SaWorkStatus.Faulted"/>).
    /// Subsequent calls to <see cref="Enqueue"/> will throw <see cref="InvalidOperationException"/>.
    /// This method is idempotent.
    /// </summary>
    Task ShutdownAsync();

    /// <summary>
    /// Synchronously shuts down the queue. Behaves identically to <see cref="ShutdownAsync"/>
    /// but blocks the calling thread until all readers complete (with a 30-second timeout).
    /// This method is idempotent.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Emergency stop: immediately cancels all reader tasks and waits for them to terminate (30-second timeout).
    /// The queue remains active — set <see cref="ConcurrencyLimit"/> to a positive value to spawn replacement readers.
    /// This method is idempotent.
    /// </summary>
    void ForceCancelReaders();

    /// <summary>
    /// Async emergency stop: immediately cancels all reader tasks and awaits their termination.
    /// The queue remains active — set <see cref="ConcurrencyLimit"/> to a positive value to spawn replacement readers.
    /// This method is idempotent.
    /// </summary>
    /// <param name="timeout">Optional maximum time to wait for readers to terminate. If <see langword="null"/>, waits indefinitely.</param>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    /// <exception cref="OperationCanceledException">If <paramref name="cancellationToken"/> is cancelled or <paramref name="timeout"/> elapses.</exception>
    Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default);
}
