namespace Sa.Schedule.Settings;

internal sealed class JobTiming(
    Func<DateTimeOffset, DateTimeOffset?> nextTime, string name) : IJobTiming
{
    public string TimingName => name;
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime) => nextTime(dateTime);

    public static IJobTiming EveryTime(TimeSpan timeSpan, string? name = null) =>
        new JobTiming(dateTime => dateTime.Add(timeSpan), name ?? $"every {timeSpan}");
}
