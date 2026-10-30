using Sa.Schedule.Cron;

namespace Sa.Schedule.Settings;

/// <summary>
/// Adapts <see cref="CronTiming"/> to the scheduler contract and, when <see cref="TimeZone"/>
/// is set, evaluates the expression on that zone's wall clock instead of UTC.
/// </summary>
internal sealed class CronTimingAdapter(CronTiming cronTiming) : IJobTiming
{
    /// <summary>
    /// The zone the cron fields are read in ("0 9 * * *" = 09:00 wall clock where you live).
    /// <c>null</c> = the scheduler's own clock (UTC). Written while the settings are being
    /// assembled (builder mutators / config overlay); the scheduler snapshots the timing by
    /// the time a job runs.
    /// </summary>
    public TimeZoneInfo? TimeZone { get; set; }

    public string TimingName => cronTiming.TimingName;

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset dateTime)
    {
        TimeZoneInfo? zone = TimeZone;

        if (zone is null)
        {
            return cronTiming.GetNextOccurrence(dateTime);
        }

        // Cron fields are wall-clock text, so the search runs on the zone's wall clock and
        // the found occurrence is converted back to the instant the scheduler's clock reads.
        // The offset is read from the zone *per occurrence* — not propagated from "now" — so
        // a DST transition between now and the next run shifts the instant by the transition
        // delta instead of drifting a full hour.
        var localNow = TimeZoneInfo.ConvertTime(dateTime, zone);

        var localNext = cronTiming.GetNextOccurrence(localNow);

        if (localNext is null) return null;

        // .DateTime is Kind.Unspecified — treat it as the zone's wall clock and re-derive the
        // offset for that wall time. An ambiguous (fall-back) local time resolves to one of
        // the two possible instants; an invalid (spring-forward gap) one shifts by the
        // transition delta — both edge cases only on the transition day itself.
        var local = localNext.Value.DateTime;

        return new DateTimeOffset(
            DateTime.SpecifyKind(local - zone.GetUtcOffset(local), DateTimeKind.Utc));
    }
}