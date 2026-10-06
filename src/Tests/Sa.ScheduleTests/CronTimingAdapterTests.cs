using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Cron;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Timezone semantics of <see cref="CronTimingAdapter"/> (finding #8): the cron expression
/// is wall-clock text ("0 9 * * *" = 09:00), so a configured zone is read on that zone's
/// wall clock and the occurrence is converted back to the instant the scheduler's clock
/// reads. The offset is taken from the zone *per occurrence*, so a DST transition between
/// now and the next run shifts the instant by the transition delta instead of an hour.
/// </summary>
public sealed class CronTimingAdapterTests
{
    private static TimeZoneInfo Berlin => TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static TimeZoneInfo Moscow => TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    [Fact]
    public void NoTimeZone_BehavesExactlyLikeBareCron()
    {
        var adapter = new CronTimingAdapter(new CronTiming("0 9 * * *"));
        var bare = new CronTiming("0 9 * * *");

        var now = new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);

        Assert.Equal(bare.GetNextOccurrence(now), adapter.GetNextOccurrence(now));
    }

    [Fact]
    public void FixedOffsetZone_FiresOnTheLocalWallClock()
    {
        // Moscow is UTC+3 with no DST: "0 9 * * *" at 00:00 UTC (= 03:00 MSK)
        // must fire at 09:00 MSK = 06:00 UTC, not 09:00 UTC.
        var adapter = new CronTimingAdapter(new CronTiming("0 9 * * *")) { TimeZone = Moscow };

        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero),
            adapter.GetNextOccurrence(now));
    }

    [Fact]
    public void DstZone_OccurrenceBeforeTransition_UsesTheCurrentOffset()
    {
        // Berlin is CET (+01:00) until the last Sunday of March 2026 (Mar 29, 02:00).
        // From Mar 27 the next 09:00 wall clock is still CET.
        var adapter = new CronTimingAdapter(new CronTiming("0 9 * * *")) { TimeZone = Berlin };

        var now = new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero); // 13:00 CET

        Assert.Equal(new DateTimeOffset(2026, 3, 28, 8, 0, 0, TimeSpan.Zero),
            adapter.GetNextOccurrence(now));
    }

    [Fact]
    public void DstZone_OccurrenceAfterTransition_ShiftsByTheTransitionDelta()
    {
        // Berlin springs forward on Mar 29, 2026 at 02:00 → 03:00 (CEST, +02:00).
        // The next 09:00 wall clock after that must land at 07:00 UTC — a plain
        // now-offset propagation would stale-drift it to 08:00 UTC instead.
        var adapter = new CronTimingAdapter(new CronTiming("0 9 * * *")) { TimeZone = Berlin };

        var now = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero); // 13:00 CET, the day before

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 7, 0, 0, TimeSpan.Zero),
            adapter.GetNextOccurrence(now));
    }

    [Fact]
    public void TimeZone_IsAppliedToCron_RegardlessOfSetterOrder()
    {
        // .WithCron(...).WithTimeZone(...) and the reverse order must both zone the timing.
        var zone = Moscow;

        var zoneFirst = JobSettings.Create<NoopJob>(Guid.NewGuid());
        zoneFirst.Properties.WithTimeZone(zone).WithCron("0 9 * * *");
        var timing1 = Assert.IsType<CronTimingAdapter>(zoneFirst.Properties.Timing);
        Assert.Same(zone, timing1.TimeZone);

        var cronFirst = JobSettings.Create<NoopJob>(Guid.NewGuid());
        cronFirst.Properties.WithCron("0 9 * * *").WithTimeZone(zone);
        var timing2 = Assert.IsType<CronTimingAdapter>(cronFirst.Properties.Timing);
        Assert.Same(zone, timing2.TimeZone);
    }

    [Fact]
    public void TimeZone_DoesNotWrapDurationTimings()
    {
        // The zone is meaningful for cron only — Every* timings are durations and
        // must keep running on the scheduler's clock.
        var zoneFirst = JobSettings.Create<NoopJob>(Guid.NewGuid());
        zoneFirst.Properties.WithTimeZone(Moscow).EveryTime(TimeSpan.FromMinutes(5));
        Assert.IsNotType<CronTimingAdapter>(zoneFirst.Properties.Timing);

        var durationFirst = JobSettings.Create<NoopJob>(Guid.NewGuid());
        durationFirst.Properties.EveryTime(TimeSpan.FromMinutes(5)).WithTimeZone(Moscow);
        Assert.IsNotType<CronTimingAdapter>(durationFirst.Properties.Timing);
    }

    [Fact]
    public void Builder_WithTimeZoneString_ResolvesAndApplies()
    {
        var services = new ServiceCollection();
        services.AddSaSchedule(b =>
            b.AddJob<NoopJob>((_, job) => job.WithCron("0 9 * * *").WithTimeZone("Europe/Moscow")));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IScheduleSettings>();

        var job = settings.GetJobSettings().Single();
        var timing = Assert.IsType<CronTimingAdapter>(job.Properties.Timing);

        Assert.Equal("Europe/Moscow", timing.TimeZone?.Id);
        Assert.Equal("Europe/Moscow", job.Properties.TimeZone?.Id);
    }

    private sealed class NoopJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}