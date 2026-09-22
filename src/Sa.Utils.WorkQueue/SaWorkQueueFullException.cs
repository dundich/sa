namespace Sa.Utils.WorkQueue;

/// <summary>
/// Thrown by <see cref="ISaWorkQueue{TInput}.Enqueue"/> or <see cref="ISaWorkQueue{TInput}.EnqueueMany"/>
/// when the queue buffer is full and the queue is configured with <see cref="SaEnqueueStrategy.Throw"/>.
/// </summary>
public class SaWorkQueueFullException : Exception
{
    /// <summary>Initializes a new instance with the default message.</summary>
    public SaWorkQueueFullException()
        : base("The work queue buffer is full.")
    {
    }

    /// <summary>Initializes a new instance with a custom message.</summary>
    public SaWorkQueueFullException(string message)
        : base(message)
    {
    }

    internal SaWorkQueueFullException(int queueCapacity, int queuedCount, string itemDisplayName)
        : base($"The work queue buffer is full ({queuedCount}/{queueCapacity}); item '{itemDisplayName}' was not enqueued.")
    {
        QueueCapacity = queueCapacity;
        QueuedCount = queuedCount;
        ItemDisplayName = itemDisplayName;
    }

    internal SaWorkQueueFullException(int queueCapacity, int queuedCount, int acceptedCount, int totalCount)
        : base($"The work queue buffer became full ({queuedCount}/{queueCapacity}) after accepting {acceptedCount} of {totalCount} items.")
    {
        QueueCapacity = queueCapacity;
        QueuedCount = queuedCount;
        AcceptedCount = acceptedCount;
        TotalCount = totalCount;
    }

    /// <summary>Gets the capacity of the queue buffer.</summary>
    public int QueueCapacity { get; private set; }

    /// <summary>Gets the number of items in the buffer when the failure was raised (equal to <see cref="QueueCapacity"/> when full).</summary>
    public int QueuedCount { get; private set; }

    /// <summary>Gets the display name of the item that could not be enqueued (single-item <see cref="ISaWorkQueue{TInput}.Enqueue"/> only).</summary>
    public string? ItemDisplayName { get; private set; }

    /// <summary>Gets the number of items accepted before the buffer became full (<see cref="ISaWorkQueue{TInput}.EnqueueMany"/> only).</summary>
    public int? AcceptedCount { get; private set; }

    /// <summary>
    /// Gets the total number of items attempted when the failure was raised
    /// (<see cref="ISaWorkQueue{TInput}.EnqueueMany"/> only). For a lazy enumerable this is
    /// <see cref="AcceptedCount"/> plus the item that could not be enqueued; items later in
    /// the collection were never enumerated.
    /// </summary>
    public int? TotalCount { get; private set; }
}
