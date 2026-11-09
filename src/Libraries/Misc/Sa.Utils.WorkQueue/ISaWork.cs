namespace Sa.Utils.WorkQueue;

/// <summary>
/// A single piece of work the queue runs, one item per call.
/// </summary>
/// <remarks>
/// <b>Called concurrently.</b> Up to <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/>
/// readers run items at the same time, so an implementation must be thread-safe — or
/// resolvable as a singleton, which is how the DI registration wires it up.
/// <para>
/// <b>Cancellation.</b> <see cref="Execute"/> receives a token that is cancelled when the
/// queue stops, when the reader is removed (limit decrease, pause — in
/// <see cref="SaReaderCancelMode.Hard"/> mode), or when a force-cancel escalates a soft
/// stop. An implementation that ignores it delays a shutdown by up to the configured
/// <see cref="SaWorkQueueOptions{TInput}.ShutdownTimeout"/>; nothing can kill the thread.
/// </para>
/// <para>
/// <b>Failures.</b> An exception leaving <see cref="Execute"/> is reported as
/// <see cref="SaWorkStatus.Faulted"/> and handed to the configured
/// <see cref="SaWorkQueueOptions{TInput}.HandleItemFaulted"/> strategy, which decides
/// whether the reader continues, stops, or the whole queue shuts down.
/// </para>
/// </remarks>
/// <typeparam name="TInput">The work item type.</typeparam>
public interface ISaWork<in TInput>
{
    /// <summary>Processes one work item.</summary>
    /// <param name="input">The item to process.</param>
    /// <param name="cancellationToken">Token that cancels the work; see the type remarks.</param>
    /// <returns>A task that completes when the item is done.</returns>
    Task Execute(TInput input, CancellationToken cancellationToken);
}
