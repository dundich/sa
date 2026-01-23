namespace Sa.Schedule.Engine;

internal sealed class JobTiming(Func<DateTimeOffset, IJobContext, DateTimeOffset?> nextTime, string name) : IJobTiming
{
    public string TimingName => name;
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime, IJobContext context) => nextTime(dateTime, context);

    public static IJobTiming EveryTime(TimeSpan timeSpan, string? name = null) =>
        new JobTiming((dateTime, _) => dateTime.Add(timeSpan), name ?? $"every {timeSpan}");

    public static IJobTiming Default { get; } = EveryTime(TimeSpan.FromSeconds(1), "default every seconds");
}
