using Sa.Schedule.Engine;

namespace Sa.Schedule.Settings;

internal sealed class JobProperies : IJobProperties
{
    public string? JobName { get; private set; }
    public bool? Immediate { get; private set; }
    public bool? IsRunOnce { get; private set; }
    public TimeSpan? InitialDelay { get; private set; }
    public bool? Disabled { get; private set; }
    public IJobTiming? Timing { get; private set; }
    public object? Tag { get; private set; }
    public int? ContextStackSize { get; private set; }

    public void WithName(string name) => JobName = name;
    public void RunOnce() => IsRunOnce = true;
    public void StartImmediate() => Immediate = true;
    public void WithInitialDelay(TimeSpan time) => InitialDelay = time;
    public void WithTiming(IJobTiming timing) => Timing = timing;
    public void SetDisabled() => Disabled = true;
    public void WithContextStackSize(int size) => ContextStackSize = size;
    public void WithTag(object tag) => Tag = tag;

    public void EveryTime(TimeSpan timeSpan, string? name = null)
        => Timing = JobTiming.EveryTime(timeSpan, name);

    internal JobProperies Merge(IJobProperties props)
    {
        JobName ??= props.JobName;
        Immediate ??= props.Immediate;
        Disabled ??= props.Disabled;
        Timing ??= props.Timing;
        IsRunOnce ??= props.IsRunOnce;
        InitialDelay ??= props.InitialDelay;
        ContextStackSize ??= props.ContextStackSize;
        Tag ??= props.Tag;
        return this;
    }
}
