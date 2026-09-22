namespace Sa.Utils.WorkQueue;

internal static class ThrowHelper
{
    public static void QueueStopped() => throw new InvalidOperationException("Queue has been stopped.");

    public static OperationCanceledException QueueShutdownException()
        => new("Queue was shut down; this work item was dropped without being processed.");

    public static OperationCanceledException CallerCancelledException()
        => new("The caller's cancellation token was cancelled; this work item was dropped without being processed.");

    public static TimeoutException ReadersTimeout(bool asynchronous, double seconds)
        => new($"Readers did not complete within {seconds:F0} seconds during {(asynchronous ? "asynchronous" : "synchronous")} shutdown.");
}
