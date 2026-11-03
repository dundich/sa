using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal static partial class JobSchedulerLog
{
    [LoggerMessage(
        EventId = 601,
        Level = LogLevel.Error,
        Message = "Job {JobId} slot {Slot} failed outside the job iteration loop; the job is aborted")]
    internal static partial void LogSlotFaulted(ILogger logger, Guid jobId, int slot, Exception error);
}
