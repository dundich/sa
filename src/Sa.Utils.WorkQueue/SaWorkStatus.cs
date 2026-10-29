namespace Sa.Utils.WorkQueue;

public enum SaWorkStatus
{
    /// <summary>
    /// in processing
    /// </summary>
    Running,
    /// <summary>
    /// it`s ok
    /// </summary>
    Completed,
    /// <summary>
    /// Unhandled technical error, may or may not be retried depending on policy.
    /// </summary>
    Faulted,
    /// <summary>
    /// Operation was cancelled by the system (timeout, shutdown, external trigger).
    /// Automatic retry is permissible.
    /// </summary>
    Cancelled,
    /// <summary>
    /// Operation was intentionally aborted by user or orchestrator.
    /// Automatic retry MUST NOT be performed.
    /// </summary>
    /// <remarks>
    /// Produced only while the queue observes the producer's token
    /// (<see cref="SaProducerCancellationMode.Observe"/>, the default). A queue configured
    /// with <see cref="SaProducerCancellationMode.Detached"/> never reports it for an
    /// accepted item: by the time the item is running, its producer token is no longer
    /// consulted. In <see cref="SaProducerCancellationMode.Observe"/> mode a drain still
    /// reports it for a buffered item whose token was already cancelled.
    /// </remarks>
    Aborted,
    /// <summary>
    /// The item was not accepted into the buffer because it was full
    /// (the <see cref="SaEnqueueStrategy.Skip"/> strategy, or the non-blocking
    /// <see cref="ISaWorkQueue{TInput}.TryEnqueue"/> / <see cref="ISaWorkQueue{TInput}.EnqueueMany"/>
    /// while the buffer was full). The item was never processed.
    /// Automatic retry is permissible.
    /// </summary>
    Skipped
}
