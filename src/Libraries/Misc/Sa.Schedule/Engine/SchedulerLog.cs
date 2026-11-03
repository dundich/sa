using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal static partial class SchedulerLog
{
    [LoggerMessage(
        EventId = 701,
        Level = LogLevel.Error,
        Message = "Job {JobId} could not be restarted; the remaining jobs were restarted")]
    internal static partial void LogJobRestartFailed(ILogger logger, Guid jobId, Exception error);
}
