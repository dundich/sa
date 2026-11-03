namespace Sa.Schedule;

/// <summary>
/// Scheduler settings served by the standard
/// <c>Microsoft.Extensions.Options</c> pipeline: binding from a configuration section
/// (<see cref="IScheduleBuilder.FromConfiguration"/> in
/// <see cref="Setup.AddSaSchedule"/>) → validation.
/// </summary>
/// <remarks>
/// The only content is the <see cref="Jobs"/> dictionary: the key is the job name, the value
/// its options. Sections that match no registered job are ignored silently; a job without a
/// configuration section keeps exactly what the code registered.
/// <para>
/// A job is matched to a <c>Jobs:&lt;key&gt;</c> section by name — <c>WithName(...)</c>, or
/// <c>typeof(Job).FullName</c> when no name was set. Renaming a job in code silently detaches
/// it from its configuration section.
/// </para>
/// <para>
/// Precedence: configuration wins over code — including <c>Disabled: false</c>, which
/// re-enables a job disabled in code.
/// </para>
/// </remarks>
public class ScheduleOptions
{
    /// <summary>
    /// The default time zone for <see cref="JobOptions.TimeZone"/> of every job that sets it
    /// neither in code (<c>WithTimeZone()</c>) nor in its own section
    /// <c>Jobs:&lt;name&gt;:TimeZone</c>. Cron expressions of such jobs are read on this
    /// zone's wall clock ("0 9 * * *" = 09:00 local), not UTC.
    /// Id is either IANA ("Europe/Moscow") or Windows ("Russian Standard Time"); .NET 8+
    /// understands both. <c>null</c> = UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// Per-job options keyed by job name. Bound from the <c>Jobs</c> subsection
    /// (for example <c>Schedule:Jobs:&lt;job name&gt;:&lt;property&gt;</c>).
    /// </summary>
    public Dictionary<string, JobOptions> Jobs { get; set; } = [];
}
