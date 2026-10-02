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
    public TimeSpan? ShutdownTimeout { get; private set; }

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

    public JobProperties Disable()
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

    /// <summary>
    /// Creates a cron-based timing from a standard 5-field cron expression.
    /// Format: "minute hour day-of-month month day-of-week"
    /// 
    /// Examples:
    ///   "0 9 * * *"       — Every day at 9:00 AM
    ///   "0 */2 * * *"    — Every 2 hours at minute 0
    ///   "30 14 * * 1-5"  — Weekdays (Mon-Fri) at 2:30 PM
    ///   "0 0 1 * *"      — First day of every month at midnight
    /// </summary>
    /// <param name="expression">The cron expression.</param>
    /// <param name="name">Optional display name.</param>
    /// <returns>An IJobTiming configured with cron scheduling.</returns>
    public JobProperties WithCron(string expression, string? name = null)
    {
        Timing = new CronTimingAdapter(new Cron.CronTiming(expression, name));
        return this;
    }

    public JobProperties WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        ConcurrencyLimit = limit;
        return this;
    }

    public JobProperties WithMaxConcurrency(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        MaxConcurrency = limit;
        return this;
    }

    public JobProperties WithShutdownTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ShutdownTimeout = timeout;
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
        ShutdownTimeout ??= props.ShutdownTimeout;

        return this;
    }
}
