namespace Sa.Utils.WorkQueue;

/// <summary>
/// Immutable options for <see cref="SaWorkQueue{TInput}"/>.
/// </summary>
/// <remarks>
/// The <c>With*</c> methods validate eagerly and are the preferred way to build options.
/// The primary constructor is public, so its arguments bypass those guards: the queue
/// constructor re-validates everything (<c>ConcurrencyLimit</c> must not be negative,
/// <c>QueueCapacity</c> must be at least 1, <c>ShutdownTimeout</c> must be positive) and throws
/// <see cref="ArgumentOutOfRangeException"/> before creating the channel. Either way, an invalid
/// value fails the same way.
/// </remarks>
public sealed record SaWorkQueueOptions<TInput>(
    ISaWork<TInput> Processor,
    int? QueueCapacity = null,
    int? ConcurrencyLimit = null,
    int? MaxConcurrency = null,
    bool? SingleWriter = false,
    SaEnqueueStrategy EnqueueStrategy = SaEnqueueStrategy.Wait,
    SaReaderCancelMode ReaderCancelMode = SaReaderCancelMode.Hard,
    SaReaderCancellationOrder ReaderCancellationOrder = SaReaderCancellationOrder.Lifo,
    Func<TInput, Exception, SaExecutionErrorStrategy>? HandleItemFaulted = null,
    Action<TInput, SaWorkStatus, Exception?>? StatusChanged = null,
    Func<TInput, string>? GetItemDisplayName = null,
    TimeSpan? ShutdownTimeout = null,
    TimeProvider? TimeProvider = null)
{
    /// <summary>Creates a new options instance with the specified queue capacity.</summary>
    /// <param name="capacity">Must be at least 1.</param>
    public SaWorkQueueOptions<TInput> WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        return this with { QueueCapacity = capacity };
    }

    /// <summary>Sets the concurrency limit (number of parallel processors).</summary>
    /// <param name="limit">
    /// <c>0</c> pauses all processing (no readers); otherwise must be positive.
    /// Values above <see cref="MaxConcurrency"/> are clamped to it.
    /// Use <c>null</c> (the default) for the automatic limit (processor count).
    /// </param>
    public SaWorkQueueOptions<TInput> WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        return this with { ConcurrencyLimit = limit };
    }

    /// <summary>Sets the absolute maximum number of reader tasks.</summary>
    /// <param name="limit">If less than 1, defaults to CPU count.</param>
    public SaWorkQueueOptions<TInput> WithMaxConcurrency(int limit)
        => this with { MaxConcurrency = limit < 1 ? Environment.ProcessorCount : limit };

    /// <summary>Optimises for a single writer source.</summary>
    public SaWorkQueueOptions<TInput> WithSingleWriter(bool sw)
        => this with { SingleWriter = sw };

    /// <summary>Registers a callback for status changes of work items.</summary>
    public SaWorkQueueOptions<TInput> WithStatusCallback(Action<TInput, SaWorkStatus, Exception?> cb)
        => this with { StatusChanged = cb };

    /// <summary>
    /// Sets a callback that decides the error handling strategy when an item fails.
    /// When not set, every failure shuts the queue down
    /// (<see cref="SaExecutionErrorStrategy.ShutdownQueue"/>).
    /// </summary>
    public SaWorkQueueOptions<TInput> WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy> cb)
        => this with { HandleItemFaulted = cb };

    /// <summary>Sets the order in which readers are cancelled when the concurrency limit decreases.</summary>
    public SaWorkQueueOptions<TInput> WithReaderCancellationOrder(SaReaderCancellationOrder order)
        => this with { ReaderCancellationOrder = order };

    /// <summary>
    /// Sets how readers are cancelled when the concurrency limit decreases or the queue is paused.
    /// </summary>
    /// <param name="mode">
    /// <see cref="SaReaderCancelMode.Hard"/> (default) interrupts the in-flight item immediately;
    /// <see cref="SaReaderCancelMode.Soft"/> lets the in-flight item finish before the reader exits.
    /// </param>
    /// <remarks>
    /// Force cancel and shutdown always interrupt in-flight work regardless of the mode.
    /// </remarks>
    public SaWorkQueueOptions<TInput> WithReaderCancelMode(SaReaderCancelMode mode)
        => this with { ReaderCancelMode = mode };

    /// <summary>
    /// Sets the strategy applied when the queue buffer is full.
    /// </summary>
    /// <param name="strategy">
    /// <see cref="SaEnqueueStrategy.Wait"/> (default) blocks <c>Enqueue</c> until space is available;
    /// <see cref="SaEnqueueStrategy.Skip"/> drops the item and <c>Enqueue</c> returns <see langword="false"/>;
    /// <see cref="SaEnqueueStrategy.Throw"/> throws <see cref="SaWorkQueueFullException"/>.
    /// A stopped or disposed queue always throws regardless of the strategy.
    /// </param>
    public SaWorkQueueOptions<TInput> WithEnqueueStrategy(SaEnqueueStrategy strategy)
        => this with { EnqueueStrategy = strategy };

    /// <summary>Sets a function to obtain a display name for each work item (e.g., for logging).</summary>
    public SaWorkQueueOptions<TInput> WithItemDisplayName(Func<TInput, string> toString)
        => this with { GetItemDisplayName = toString };

    /// <summary>Sets the maximum time to wait for readers during synchronous shutdown or force-cancel.</summary>
    /// <param name="timeout">Must be positive. If not set, defaults to 30 seconds.</param>
    public SaWorkQueueOptions<TInput> WithShutdownTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        return this with { ShutdownTimeout = timeout };
    }

    /// <summary>Sets the clock behind every bounded wait inside the queue.</summary>
    /// <param name="timeProvider">
    /// Defaults to <see cref="System.TimeProvider.System"/>. Only tests have a reason to
    /// change it: <see cref="ShutdownTimeout"/> defaults to 30 seconds, and every timeout
    /// path — a reader that ignores cancellation, a force-cancel that never unwinds — is
    /// otherwise only reachable by sleeping through it. Point this at a controllable
    /// clock and those paths complete as soon as the test advances time.
    /// </summary>
    /// <remarks>
    /// A production caller should leave this alone. It governs waits only, never the
    /// processors themselves, so a clock that jumps cannot corrupt a queue's state.
    /// </remarks>
    public SaWorkQueueOptions<TInput> WithTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return this with { TimeProvider = timeProvider };
    }


    /// <summary>Creates options from a delegate that processes a single item.</summary>
    /// <param name="process">Async delegate that receives the item and a cancellation token.</param>
    public static SaWorkQueueOptions<TInput> Create(Func<TInput, CancellationToken, Task> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new(new DelegatingWork(process));
    }

    /// <summary>Creates options from an <see cref="ISaWork{TInput}"/> processor.</summary>
    public static SaWorkQueueOptions<TInput> Create(ISaWork<TInput> processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        return new(processor);
    }

    // Helper adapter from delegate to ISaWork
    private sealed class DelegatingWork(Func<TInput, CancellationToken, Task> process) : ISaWork<TInput>
    {
        public Task Execute(TInput input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }
}

