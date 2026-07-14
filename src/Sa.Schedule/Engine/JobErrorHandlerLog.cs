using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal static partial class JobErrorHandlerLog
{
    [LoggerMessage(
        EventId = 501,
        Level = LogLevel.Error,
        Message = "* Job '{JobName}' failed with unexpected error: {Error}")]
    internal static partial void LogUnknownJobError(this ILogger logger, string jobName, string error);

    [LoggerMessage(
        EventId = 502,
        Level = LogLevel.Error,
        Message = "* Job '{JobName}' aborted due to error: {Error}")]
    internal static partial void LogAbortJob(this ILogger logger, string jobName, string error);

    [LoggerMessage(
        EventId = 503,
        Level = LogLevel.Critical,
        Message = "* Job '{JobName}' triggered application shutdown due to error: {Error}")]
    internal static partial void LogCloseApplication(this ILogger logger, string jobName, string error);

    [LoggerMessage(
        EventId = 504,
        Level = LogLevel.Error,
        Message = "* Job '{JobName}' triggered stop of all jobs due to error: {Error}")]
    internal static partial void LogStopAllJobs(this ILogger logger, string jobName, string error);
}
