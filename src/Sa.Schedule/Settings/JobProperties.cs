using Sa.Schedule.Engine;

namespace Sa.Schedule.Settings;

internal sealed class JobProperties : IJobProperties
{
    public string? JobName { get; private set; }
    public bool? Immediate { get; private set; }
    public bool? IsRunOnce { get; private set; }
    public TimeSpan? InitialDelay { get; private set; }
    public bool? Disabled { get; private set; }
    public IJobTiming? Timing { get; private set; }
    public object? Tag { get; private set; }
    public int? ContextStackSize { get; private set; }
    public int? ConcurrencyLimit { get; private set; }
    public int? MaxConcurrency { get; private set; }

    public JobProperties WithName(string name)
    {
        JobName = name;
        return this;
    }

    public JobProperties RunOnce()
    {
        IsRunOnce = true;
        return this;
    }

    public JobProperties StartImmediate()
    {
        Immediate = true;
        return this;
    }

    public JobProperties WithInitialDelay(TimeSpan time)
    {
        InitialDelay = time;
        return this;
    }

    public JobProperties WithTiming(IJobTiming timing)
    {
        Timing = timing;
        return this;
    }

    public JobProperties SetDisabled()
    {
        Disabled = true;
        return this;
    }

    public JobProperties WithContextStackSize(int size)
    {
        ContextStackSize = size;
        return this;
    }

    public JobProperties WithTag(object tag)
    {
        Tag = tag;
        return this;
    }

    public JobProperties EveryTime(TimeSpan timeSpan, string? name = null)
    {
        Timing = JobTiming.EveryTime(timeSpan, name);
        return this;
    }

    public JobProperties WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        ConcurrencyLimit = limit;
        return this;
    }

    public JobProperties WithMaxConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        MaxConcurrency = limit;
        return this;
    }


    internal JobProperties Merge(IJobProperties props)
    {
        JobName ??= props.JobName;
        Immediate ??= props.Immediate;
        Disabled ??= props.Disabled;
        Timing ??= props.Timing;
        IsRunOnce ??= props.IsRunOnce;
        InitialDelay ??= props.InitialDelay;
        ContextStackSize ??= props.ContextStackSize;
        Tag ??= props.Tag;

        ConcurrencyLimit ??= props.ConcurrencyLimit;
        MaxConcurrency ??= props.MaxConcurrency;

        return this;
    }
}
