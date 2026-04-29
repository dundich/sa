namespace Sa.Utils.WorkQueue;

using System.Threading.Channels;

public sealed record SaWorkQueueOptions<TInput>(
    ISaWork<TInput> Processor,
    int? QueueCapacity = null,
    int? ConcurrencyLimit = null,
    int? MaxConcurrency = null,
    bool? SingleWriter = false,
    Func<TInput, Exception, SaExecutionErrorStrategy>? HandleItemFaulted = null,
    Action<TInput, SaWorkStatus, Exception?>? StatusChanged = null,
    BoundedChannelFullMode FullMode = BoundedChannelFullMode.Wait,
    SaReaderScalingStrategy ReaderScalingStrategy = SaReaderScalingStrategy.Lifo,
    Func<TInput, string>? GetItemDisplayName = null)
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

    /// <summary>Sets the behaviour when the queue is full.</summary>
    public SaWorkQueueOptions<TInput> WithFullMode(BoundedChannelFullMode mode)
        => this with { FullMode = mode };

    /// <summary>Sets the strategy for assigning items to readers.</summary>
    public SaWorkQueueOptions<TInput> WithReaderScalingStrategy(SaReaderScalingStrategy strategy)
        => this with { ReaderScalingStrategy = strategy };

    /// <summary>Sets a function to obtain a display name for each work item (e.g., for logging).</summary>
    public SaWorkQueueOptions<TInput> WithItemDisplayName(Func<TInput, string> toString)
        => this with { GetItemDisplayName = toString };


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

