namespace Sa.Utils.WorkQueue;

public enum SaEnqueueStrategy
{
    /// <summary>
    /// Wait (default): <see cref="ISaWorkQueue{TInput}.Enqueue"/> blocks until space is available in the buffer.
    /// </summary>
    /// <remarks>
    /// This is the original behavior: the bounded channel applies back-pressure to the producer.
    /// The call completes with <see langword="true" /> once the item is accepted into the buffer.
    /// </remarks>
    Wait = 0,

    /// <summary>
    /// Skip: when the buffer is full, the item is dropped and <see cref="ISaWorkQueue{TInput}.Enqueue"/>
    /// returns <see langword="false" /> without blocking.
    /// </summary>
    /// <remarks>
    /// Useful for fire-and-forget producers (metrics, telemetry, logs) where back-pressure is unacceptable.
    /// Dropped items are not reported via the status callback and are not counted in
    /// <see cref="ISaWorkQueue{TInput}.QueueTasks"/>.
    /// </remarks>
    Skip = 1,

    /// <summary>
    /// Throw: when the buffer is full, <see cref="ISaWorkQueue{TInput}.Enqueue"/> throws
    /// <see cref="SaWorkQueueFullException"/> without blocking.
    /// </summary>
    /// <remarks>
    /// Useful when the producer must react explicitly to overload instead of blocking or dropping silently.
    /// </remarks>
    Throw = 2
}
