namespace Sa.Utils.WorkQueue;

using Microsoft.Extensions.Logging;

internal static partial class SaWorkQueueLogMessages
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "[{Item}] processing was cancelled")]
    public static partial void ItemCancelled(ILogger logger, string item, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "[{Item}] processing was aborted")]
    public static partial void ItemAborted(ILogger logger, string item, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "[{Item}] execution failed")]
    public static partial void ItemExecutionFailed(ILogger logger, string item, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "[{Item}] event handler failed for work item")]
    public static partial void ItemHandlerFailed(ILogger logger, string item, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "ReaderTask error")]
    public static partial void ReaderError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error during shutdown")]
    public static partial void ShutdownError(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Reader terminated unexpectedly. Effective concurrency is now {Concurrency}. Set ConcurrencyLimit to restore readers.")]
    public static partial void ReaderLost(ILogger logger, int concurrency);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Force-cancel stopped waiting after {Seconds:F0}s with {Unfinished} reader(s) still unwinding. " +
                  "The {Pending} item(s) left in the buffer were kept, not dropped: set ConcurrencyLimit to process them.")]
    public static partial void ForceCancelTimedOut(ILogger logger, double seconds, int unfinished, int pending);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "Force-cancel stopped waiting: the caller's token was cancelled with {Unfinished} reader(s) still unwinding. " +
                  "The {Pending} item(s) left in the buffer were kept, not dropped: set ConcurrencyLimit to process them.")]
    public static partial void ForceCancelWaitCancelled(ILogger logger, int unfinished, int pending);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "WaitForIdleAsync returned with {Pending} item(s) still pending: {Reason}. " +
                  "The queue cannot drain them on its own; set ConcurrencyLimit above 0 to resume.")]
    public static partial void IdleWaitGaveUp(ILogger logger, int pending, SaWorkNoProgress reason);

    public static void LogReaderLost(ILogger? logger, int concurrency)
    {
        if (logger is not null) ReaderLost(logger, concurrency);
    }

    public static void LogItemCancelled(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) ItemCancelled(logger, item, ex);
    }

    public static void LogItemAborted(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) ItemAborted(logger, item, ex);
    }

    public static void LogItemExecutionFailed(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) ItemExecutionFailed(logger, item, ex);
    }

    public static void LogItemHandlerFailed(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) ItemHandlerFailed(logger, item, ex);
    }

    public static void LogReaderError(ILogger? logger, Exception ex)
    {
        if (logger is not null) ReaderError(logger, ex);
    }

    public static void LogShutdownError(ILogger? logger, Exception ex)
    {
        if (logger is not null) ShutdownError(logger, ex);
    }

    public static void LogForceCancelTimedOut(ILogger? logger, double seconds, int unfinished, int pending)
    {
        if (logger is not null) ForceCancelTimedOut(logger, seconds, unfinished, pending);
    }

    public static void LogForceCancelWaitCancelled(ILogger? logger, int unfinished, int pending)
    {
        if (logger is not null) ForceCancelWaitCancelled(logger, unfinished, pending);
    }

    public static void LogIdleWaitGaveUp(ILogger? logger, int pending, SaWorkNoProgress reason)
    {
        if (logger is not null) IdleWaitGaveUp(logger, pending, reason);
    }
}
