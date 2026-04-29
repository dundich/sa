namespace Sa.Utils.WorkQueue;

/// <summary>
/// Asynchronous task queue with limited parallelism.
/// </summary>
public interface ISaWorkQueue<in TInput> : IDisposable, IAsyncDisposable
{
    bool IsEnabled { get; }
    int QueueTasks { get; }
    bool IsIdle();
    int ConcurrencyLimit { get; set; }
    int MaxConcurrency { get; }
    int QueueCapacity { get; }

    Exception? ShutdownError { get; }

    ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default);
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync();
    void ForceCancelReaders();
}
