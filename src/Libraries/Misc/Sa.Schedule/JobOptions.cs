namespace Sa.Schedule;

/// <summary>
/// Options of a single job from <see cref="ScheduleOptions.Jobs"/>.
/// Every property is nullable: "the property is absent from configuration" means
/// "not overridden" — the job keeps the configuration it was registered with in code.
/// </summary>
public class JobOptions
{
    /// <summary>
    /// Enables or disables the job. Overrides <c>Disable()</c> from code in both
    /// directions: <c>false</c> enables a job that was disabled in code.
    /// </summary>
    public bool? Disabled { get; set; }

    /// <summary>
    /// Runs the job immediately on the scheduler's first tick, without waiting for its
    /// timing. Overrides <c>StartImmediate()</c> from code in both directions: <c>false</c>
    /// cancels an immediate run enabled in code.
    /// </summary>
    public bool? Immediate { get; set; }

    /// <summary>
    /// Runs the job exactly once: after the first run the scheduler never starts it again.
    /// Overrides <c>RunOnce()</c> from code in both directions: <c>false</c> returns the job
    /// to its periodic schedule.
    /// </summary>
    public bool? IsRunOnce { get; set; }

    /// <summary>
    /// A cron expression (standard 5 fields) — replaces any timing set in code.
    /// Mutually exclusive with <see cref="Every"/>: setting both is a validation error.
    /// Surrounding whitespace is allowed — the parser trims the expression.
    /// </summary>
    public string? Cron { get; set; }

    /// <summary>
    /// The execution interval — replaces any timing set in code. Uses the constant
    /// <c>TimeSpan</c> form, for example <c>00:05:00</c>. Must be greater than zero.
    /// </summary>
    public TimeSpan? Every { get; set; }

    /// <summary>
    /// The time zone the job's <see cref="Cron"/> is read in: the expression acquires the
    /// meaning of that zone's wall clock ("0 9 * * *" is 09:00 where you live), not UTC.
    /// Replaces <c>WithTimeZone()</c> from code; when the job has no <c>TimeZone</c> of its
    /// own, <see cref="ScheduleOptions.TimeZone"/> (scheduler-wide) applies.
    /// Id is either IANA ("Europe/Moscow") or Windows ("Russian Standard Time"); .NET 8+
    /// understands both. Intervals (<see cref="Every"/>) are unaffected by the time zone.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Delay before the first run. Uses the constant <c>TimeSpan</c> form, for example
    /// <c>00:00:30</c>. Must not be negative.
    /// </summary>
    public TimeSpan? InitialDelay { get; set; }

    /// <summary>
    /// How many slots of the job are active at any moment. Must not be negative.
    /// When greater than <see cref="MaxConcurrency"/>, the scheduler clamps it down to
    /// <see cref="MaxConcurrency"/>.
    /// </summary>
    public int? ConcurrencyLimit { get; set; }

    /// <summary>
    /// The absolute maximum number of slots allocated for the job. Must be at least 1.
    /// </summary>
    public int? MaxConcurrency { get; set; }
}
