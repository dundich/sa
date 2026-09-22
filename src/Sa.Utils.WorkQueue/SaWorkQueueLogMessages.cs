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
}
