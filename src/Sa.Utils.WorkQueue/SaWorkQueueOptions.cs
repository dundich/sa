namespace Sa.Utils.WorkQueue;

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
    TimeSpan? ShutdownTimeout = null)
{
    /// <summary>Creates a new options instance with the specified queue capacity.</summary>
    /// <param name="capacity">Must be at least 1.</param>
    public SaWorkQueueOptions<TInput> WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        return this with { QueueCapacity = capacity };
    }

    /// <summary>Sets the concurrency limit (number of parallel processors).</summary>
    /// <param name="limit">0 means unlimited, otherwise must be positive.</param>
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

    /// <summary>Sets a callback that decides the error handling strategy when an item fails.</summary>
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


    /// <summary>Creates options from a delegate that processes a single item.</summary>
    /// <param name="process">Async delegate that receives the item and a cancellation token.</param>
    public static SaWorkQueueOptions<TInput> Create(Func<TInput, CancellationToken, Task> process)
        => new(new DelegatingWork(process));

    /// <summary>Creates options from an <see cref="ISaWork{TInput}"/> processor.</summary>
    public static SaWorkQueueOptions<TInput> Create(ISaWork<TInput> processor) => new(processor);

    // Helper adapter from delegate to ISaWork
    private sealed class DelegatingWork(Func<TInput, CancellationToken, Task> process) : ISaWork<TInput>
    {
        public Task Execute(TInput input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }
}

