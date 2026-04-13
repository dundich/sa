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
    public int? QueueCapacity { get; private set; }
    public int? ConcurrencyLimit { get; private set; }
    public int? MaxConcurrencyLimit { get; private set; }
    public bool? SingleWriter { get; private set; }

    public IJobProperties WithName(string name)
    {
        JobName = name;
        return this;
    }

    public IJobProperties RunOnce()
    {
        IsRunOnce = true;
        return this;
    }

    public IJobProperties StartImmediate()
    {
        Immediate = true;
        return this;
    }

    public IJobProperties WithInitialDelay(TimeSpan time)
    {
        InitialDelay = time;
        return this;
    }

    public IJobProperties WithTiming(IJobTiming timing)
    {
        Timing = timing;
        return this;
    }

    public IJobProperties SetDisabled()
    {
        Disabled = true;
        return this;
    }

    public IJobProperties WithContextStackSize(int size)
    {
        ContextStackSize = size;
        return this;
    }

    public IJobProperties WithTag(object tag)
    {
        Tag = tag;
        return this;
    }

    public IJobProperties EveryTime(TimeSpan timeSpan, string? name = null)
    {
        Timing = JobTiming.EveryTime(timeSpan, name);
        return this;
    }

    public IJobProperties WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        QueueCapacity = capacity;
        return this;
    }

    public IJobProperties WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        ConcurrencyLimit = limit;
        return this;
    }

    public IJobProperties WithMaxConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        MaxConcurrencyLimit = limit;
        return this;
    }

    public IJobProperties WithSingleWriter(bool singleWriter)
    {
        SingleWriter = singleWriter;
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
        QueueCapacity ??= props.QueueCapacity;
        ConcurrencyLimit ??= props.ConcurrencyLimit;
        MaxConcurrencyLimit ??= props.MaxConcurrencyLimit;
        SingleWriter ??= props.SingleWriter;

        return this;
    }
}
