namespace Sa.Utils.WorkQueue;

/// <summary>What an enqueue call does when the buffer is full.</summary>
public enum SaEnqueueStrategy
{
    /// <summary>
    /// Wait (default): <see cref="ISaWorkChannel{TInput}.Enqueue"/> blocks until space is available in the buffer.
    /// </summary>
    /// <remarks>
    /// This is the original behavior: the bounded channel applies back-pressure to the producer.
    /// The call completes with <see langword="true" /> once the item is accepted into the buffer.
    /// </remarks>
    Wait = 0,

    /// <summary>
    /// Skip: when the buffer is full, the item is dropped and <see cref="ISaWorkChannel{TInput}.Enqueue"/>
    /// returns <see langword="false" /> without blocking.
    /// </summary>
    /// <remarks>
    /// Useful for fire-and-forget producers (metrics, telemetry, logs) where back-pressure is unacceptable.
    /// Dropped items are reported as <see cref="SaWorkStatus.Skipped"/> via the status callback
    /// and are not counted in <see cref="ISaWorkChannel{TInput}.QueueTasks"/>.
    /// </remarks>
    Skip = 1,

    /// <summary>
    /// Throw: when the buffer is full, <see cref="ISaWorkChannel{TInput}.Enqueue"/> throws
    /// <see cref="SaWorkQueueFullException"/> without blocking.
    /// </summary>
    /// <remarks>
    /// Useful when the producer must react explicitly to overload instead of blocking or dropping silently.
    /// </remarks>
    Throw = 2
}
