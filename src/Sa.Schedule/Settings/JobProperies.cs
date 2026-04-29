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
    public int? ConcurrencyLimit { get; private set; }
    public int? MaxConcurrency { get; private set; }

    public JobProperies WithName(string name)
    {
        JobName = name;
        return this;
    }

    public JobProperies RunOnce()
    {
        IsRunOnce = true;
        return this;
    }

    public JobProperies StartImmediate()
    {
        Immediate = true;
        return this;
    }

    public JobProperies WithInitialDelay(TimeSpan time)
    {
        InitialDelay = time;
        return this;
    }

    public JobProperies WithTiming(IJobTiming timing)
    {
        Timing = timing;
        return this;
    }

    public JobProperies SetDisabled()
    {
        Disabled = true;
        return this;
    }

    public JobProperies WithContextStackSize(int size)
    {
        ContextStackSize = size;
        return this;
    }

    public JobProperies WithTag(object tag)
    {
        Tag = tag;
        return this;
    }

    public JobProperies EveryTime(TimeSpan timeSpan, string? name = null)
    {
        Timing = JobTiming.EveryTime(timeSpan, name);
        return this;
    }

    public JobProperies WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        ConcurrencyLimit = limit;
        return this;
    }

    public JobProperies WithMaxConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        MaxConcurrency = limit;
        return this;
    }


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

        ConcurrencyLimit ??= props.ConcurrencyLimit;
        MaxConcurrency ??= props.MaxConcurrency;

        return this;
    }
}
