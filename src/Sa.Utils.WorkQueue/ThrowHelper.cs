namespace Sa.Utils.WorkQueue;

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

internal static class ThrowHelper
{
    /// <summary>
    /// Reports that the queue will not accept more work. Pass the cause where one
    /// exists — completing the channel writer surfaces as an
    /// <see cref="ChannelClosedException"/>, which tells the caller nothing about
    /// why the queue stopped.
    /// </summary>
    [DoesNotReturn]
    public static void QueueStopped(Exception? cause = null)
        => throw new InvalidOperationException("Queue has been stopped.", cause);

    [DoesNotReturn]
    public static void QueuePaused() => throw new InvalidOperationException(
        "Queue is paused (ConcurrencyLimit is 0) and still has pending items; no reader can process them. " +
        "Raise ConcurrencyLimit above 0, or wait without failIfNoProgress to return immediately.");

    [DoesNotReturn]
    public static void QueueHasNoReaders() => throw new InvalidOperationException(
        "Queue has no live readers and still has pending items, so they can never be processed. " +
        "Set ConcurrencyLimit to spawn replacement readers, or wait without failIfNoProgress to return immediately.");

    public static OperationCanceledException QueueShutdownException()
        => new("Queue was shut down; this work item was dropped without being processed.");

    public static OperationCanceledException CallerCancelledException()
        => new("The caller's cancellation token was cancelled; this work item was dropped without being processed.");

    public static OperationCanceledException DroppedException(SaWorkDrainReason reason)
        => reason == SaWorkDrainReason.ForceCancel
            ? new("All readers were force-cancelled; this work item was dropped without being processed.")
            : QueueShutdownException();

    public static TimeoutException ReadersTimeout(bool asynchronous, double seconds)
        => new($"Readers did not complete within {seconds:F0} seconds during {(asynchronous ? "asynchronous" : "synchronous")} shutdown.");
}
