using Sa.Schedule.Cron;

namespace Sa.Schedule.Settings;

internal sealed class CronTimingAdapter(CronTiming cronTiming) : IJobTiming
{
    public string TimingName => cronTiming.TimingName;

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime) => cronTiming.GetNextOccurrence(dateTime);
}
