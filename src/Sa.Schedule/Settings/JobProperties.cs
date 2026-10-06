using Sa.Schedule.Cron;

namespace Sa.Schedule.Settings;

internal sealed class JobProperties : IJobProperties
{
    public string? JobName { get; private set; }
    public bool? Immediate { get; private set; }
    public bool? IsRunOnce { get; private set; }
    public TimeSpan? InitialDelay { get; private set; }
    public bool? Disabled { get; private set; }
    public IJobTiming? Timing { get; private set; }
    public TimeZoneInfo? TimeZone { get; private set; }
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
        ApplyTimeZoneToTiming();
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
        ApplyTimeZoneToTiming();
        return this;
    }

    /// <summary>
    /// Sets the time zone the cron expression is read in — see <see cref="TimeZone"/>.
    /// Applies to cron timings only; durations and custom timings are unaffected.
    /// </summary>
    public JobProperties WithTimeZone(TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        TimeZone = timeZone;
        ApplyTimeZoneToTiming();
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

    /// <summary>
    /// Applies <see cref="JobOptions"/> from configuration on top of whatever the code set:
    /// configuration wins in every field, in both directions — including
    /// <c>Disabled = false</c> re-enabling a job disabled in code. A null property leaves the
    /// code value untouched. <paramref name="options"/> may be null when a schedule-wide
    /// <c>TimeZone</c> default still needs to reach this job.
    /// </summary>
    internal void ApplyConfiguration(JobOptions? options, TimeZoneInfo? defaultTimeZone = null)
    {
        // Zone precedence: per-job config → schedule-wide default → code value. A string id
        // was already accepted by the options validator (which runs before any settings are
        // consumed from the pipeline), so the resolve here cannot fail on that path.
        if (options?.TimeZone is not null)
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        }
        else if (TimeZone is null && defaultTimeZone is not null)
        {
            TimeZone = defaultTimeZone;
        }

        if (options is null)
        {
            ApplyTimeZoneToTiming();
            return;
        }

        if (options.Disabled is not null)
        {
            Disabled = options.Disabled;
        }

        if (options.Immediate is not null)
        {
            Immediate = options.Immediate;
        }

        if (options.IsRunOnce is not null)
        {
            IsRunOnce = options.IsRunOnce;
        }

        if (options.ConcurrencyLimit is not null)
        {
            ConcurrencyLimit = options.ConcurrencyLimit;
        }

        if (options.MaxConcurrency is not null)
        {
            MaxConcurrency = options.MaxConcurrency;
        }

        if (options.Every is not null)
        {
            Timing = JobTiming.EveryTime(options.Every.Value);
        }
        else if (options.Cron is not null)
        {
            Timing = new CronTimingAdapter(new CronTiming(options.Cron));
        }

        if (options.InitialDelay is not null)
        {
            InitialDelay = options.InitialDelay;
        }

        // The config may have changed either the zone or the cron timing — re-apply so the
        // pairing stays correct regardless of the order they arrived in.
        ApplyTimeZoneToTiming();
    }

    /// <summary>
    /// Applies <see cref="TimeZone"/> to the current timing. Only cron carries wall-clock
    /// semantics — durations (<c>Every*</c>) and custom timings keep running on the
    /// scheduler's clock. Called after every point that can change either value, so zone and
    /// cron can be set in any order.
    /// </summary>
    private void ApplyTimeZoneToTiming()
    {
        if (TimeZone is not null && Timing is CronTimingAdapter cron)
        {
            cron.TimeZone = TimeZone;
        }
    }


    internal JobProperties Merge(IJobProperties props)
    {
        JobName ??= props.JobName;
        Immediate ??= props.Immediate;
        Disabled ??= props.Disabled;
        Timing ??= props.Timing;
        IsRunOnce ??= props.IsRunOnce;
        TimeZone ??= props.TimeZone;
        InitialDelay ??= props.InitialDelay;
        ContextStackSize ??= props.ContextStackSize;
        Tag ??= props.Tag;

        ConcurrencyLimit ??= props.ConcurrencyLimit;
        MaxConcurrency ??= props.MaxConcurrency;
        ShutdownTimeout ??= props.ShutdownTimeout;

        ApplyTimeZoneToTiming();

        return this;
    }
}
