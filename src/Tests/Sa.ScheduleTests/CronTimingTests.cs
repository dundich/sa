using Sa.Schedule.Engine;
using System.Globalization;

namespace Sa.ScheduleTests;

public class CronTimingTests
{
    [Theory]
    [InlineData("* * * * *", "2026-06-25T10:30:00Z", "2026-06-25T10:31:00Z")]
    [InlineData("0 9 * * *", "2026-06-25T08:00:00Z", "2026-06-25T09:00:00Z")]
    [InlineData("0 9 * * *", "2026-06-25T09:00:00Z", "2026-06-26T09:00:00Z")]
    [InlineData("0 9 * * *", "2026-06-25T09:01:00Z", "2026-06-26T09:00:00Z")]
    [InlineData("30 14 * * 1-5", "2026-06-25T14:30:00Z", "2026-06-26T14:30:00Z")] // Friday -> Monday
    [InlineData("0 0 1 * *", "2026-06-25T00:00:00Z", "2026-07-01T00:00:00Z")]
    [InlineData("0 */2 * * *", "2026-06-25T10:00:00Z", "2026-06-25T12:00:00Z")]
    [InlineData("15 10 * * *", "2026-06-25T10:14:00Z", "2026-06-25T10:15:00Z")]
    [InlineData("0 0 1 1 *", "2026-06-25T00:00:00Z", "2027-01-01T00:00:00Z")]
    public void GetNextOccurrence_ReturnsCorrectNextTime(string cronExpression, string startTime, string expectedNext)
    {
        var timing = new CronTiming(cronExpression);
        var start = DateTimeOffset.Parse(startTime, CultureInfo.InvariantCulture);
        var expected = DateTimeOffset.Parse(expectedNext, CultureInfo.InvariantCulture);

        var result = timing.GetNextOccurrence(start, null!);

        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetNextOccurrence_EveryMinute_NextMinute()
    {
        var timing = new CronTiming("* * * * *");
        var start = new DateTimeOffset(2026, 6, 25, 10, 30, 45, TimeSpan.Zero);

        var result = timing.GetNextOccurrence(start, null!);

        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 31, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextOccurrence_HourlyAtMinute0_NextHour()
    {
        var timing = new CronTiming("0 * * * *");
        var start = new DateTimeOffset(2026, 6, 25, 10, 15, 0, TimeSpan.Zero);

        var result = timing.GetNextOccurrence(start, null!);

        Assert.Equal(new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextOccurrence_Every2Hours_CorrectIntervals()
    {
        var timing = new CronTiming("0 */2 * * *");

        // Test multiple intervals
        DateTimeOffset t1 = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset t2 = new(2026, 6, 25, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset t3 = new(2026, 6, 25, 4, 0, 0, TimeSpan.Zero);
        DateTimeOffset t4 = new(2026, 6, 25, 22, 0, 0, TimeSpan.Zero);

        Assert.Equal(t2, timing.GetNextOccurrence(t1, null!));
        Assert.Equal(t3, timing.GetNextOccurrence(t2, null!));
        // From 04:00 → next slot is 06:00 same day
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 6, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(t3, null!));
        // From 22:00 → next slot is 00:00 next day
        Assert.Equal(new DateTimeOffset(2026, 6, 26, 0, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(t4, null!));
    }

    [Fact]
    public void GetNextOccurrence_WeekdayOnly_SkipsWeekend()
    {
        var timing = new CronTiming("0 9 * * 1-5"); // Mon-Fri at 9 AM

        // Friday June 26, 2026
        var friday = new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);
        var nextMonday = new DateTimeOffset(2026, 6, 29, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(nextMonday, timing.GetNextOccurrence(friday, null!));
    }

    [Fact]
    public void GetNextOccurrence_FirstDayOfMonth_CorrectMonthTransition()
    {
        var timing = new CronTiming("0 0 1 * *");

        var june25 = new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero);
        var july1 = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(july1, timing.GetNextOccurrence(june25, null!));
    }

    [Fact]
    public void GetNextOccurrence_CommaSeparatedMinutes_MultipleValues()
    {
        var timing = new CronTiming("10,30,50 * * * *");
        var start = new DateTimeOffset(2026, 6, 25, 10, 15, 0, TimeSpan.Zero);

        var result = timing.GetNextOccurrence(start, null!);

        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void GetNextOccurrence_RangeOfDays_MatchesCorrectly()
    {
        var timing = new CronTiming("0 12 1-15 * *"); // Noon on 1st-15th

        var midMonth = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        // After June 20, next valid day is July 1 (1-15 range includes the 1st)
        var nextMonth = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(nextMonth, timing.GetNextOccurrence(midMonth, null!));
    }

    [Fact]
    public void GetNextOccurrence_StepInRange_CorrectIntervals()
    {
        var timing = new CronTiming("0 9-17/2 * * *"); // Every 2 hours from 9-17 → slots: 9,11,13,15,17

        var morning = new DateTimeOffset(2026, 6, 25, 9, 0, 0, TimeSpan.Zero);
        var noon = new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero);
        var afternoon = new DateTimeOffset(2026, 6, 25, 17, 0, 0, TimeSpan.Zero);

        // From 9:00 → next slot is 11:00
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(morning, null!));
        // From 11:00 → next slot is 13:00 (not 17!)
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 13, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(noon, null!));
        // From 17:00 → next slot is 9:00 next day
        Assert.Equal(new DateTimeOffset(2026, 6, 26, 9, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(afternoon, null!));
    }

    [Fact]
    public void GetNextOccurrence_LargeInterval_CrossesYearBoundary()
    {
        var timing = new CronTiming("0 0 29 2 *"); // Leap day

        var normalYear = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        var leapDay2028 = new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(leapDay2028, timing.GetNextOccurrence(normalYear, null!));
    }

    [Fact]
    public void TimingName_UsesCustomName_WhenProvided()
    {
        var timing = new CronTiming("0 9 * * *", "Daily at 9 AM");
        Assert.Equal("Daily at 9 AM", timing.TimingName);
    }

    [Fact]
    public void TimingName_UsesDefaultName_WhenNotProvided()
    {
        var timing = new CronTiming("0 9 * * *");
        Assert.Equal("cron", timing.TimingName);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("* *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 32 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 7")]
    [InlineData("abc * * * *")]
    public void Constructor_ThrowsFormatException_ForInvalidExpression(string invalidExpression)
    {
        CronTiming act() => new(invalidExpression);
        Assert.Throws<FormatException>((Func<CronTiming>)act);
    }

    [Fact]
    public void Constructor_NullExpression_ThrowsArgumentException()
    {
        Assert.ThrowsAny<Exception>(() => new CronTiming(null!));
    }

    [Fact]
    public void StaticEvery_Method_CreatesCronTimingWithName()
    {
        var timing = CronTiming.Every("0 9 * * *", "Morning job");

        Assert.Equal("Morning job", timing.TimingName);
    }

    [Fact]
    public void GetNextOccurrence_Timezone_Aware()
    {
        // Test with timezone offset
        var tzOffset = TimeSpan.FromHours(2);
        var timing = new CronTiming("0 10 * * *");

        var dt = new DateTimeOffset(2026, 6, 25, 9, 30, 0, tzOffset);
        var expected = new DateTimeOffset(2026, 6, 25, 10, 0, 0, tzOffset);

        Assert.Equal(expected, timing.GetNextOccurrence(dt, null!));
    }

    [Fact]
    public void GetNextOccurrence_BothDayConstraints_BothMustMatch()
    {
        // Day of month AND day of week both specified — both must match
        var timing = new CronTiming("0 12 15 * 3"); // Wednesday (3) on 15th

        // Find a date that's both the 15th and a Wednesday
        // June 15, 2026 is a Tuesday, so it should skip
        var start = new DateTimeOffset(2026, 6, 15, 13, 0, 0, TimeSpan.Zero);

        // July 15, 2026 is a Wednesday
        var expected = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, timing.GetNextOccurrence(start, null!));
    }

    [Fact]
    public void GetNextOccurrence_MinuteStep_CorrectIntervals()
    {
        var timing = new CronTiming("*/15 * * * *"); // Every 15 minutes: 0, 15, 30, 45

        var start = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 15, 0, TimeSpan.Zero), timing.GetNextOccurrence(start, null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 30, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 10, 15, 0, TimeSpan.Zero), null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 10, 45, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 10, 30, 0, TimeSpan.Zero), null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 10, 45, 0, TimeSpan.Zero), null!));
    }

    [Fact]
    public void GetNextOccurrence_HourRangeWithStep_CorrectSlots()
    {
        // Every 3 hours from 8-18 → slots: 8, 11, 14, 17
        var timing = new CronTiming("0 8-17/3 * * *");

        var start = new DateTimeOffset(2026, 6, 25, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(start, null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 14, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 11, 0, 0, TimeSpan.Zero), null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 17, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 14, 0, 0, TimeSpan.Zero), null!));
        Assert.Equal(new DateTimeOffset(2026, 6, 26, 8, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(new DateTimeOffset(2026, 6, 25, 17, 0, 0, TimeSpan.Zero), null!));
    }

    [Fact]
    public void GetNextOccurrence_WildcardMonth_AnyMonth()
    {
        var timing = new CronTiming("0 0 29 2 *"); // Feb 29 only

        var jan2028 = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(jan2028, null!));

        // From within 2028 (after Feb 29), next leap day is 2032 — but that's 4 years away
        // The 2-year search limit means this returns null
        var mar2028 = new DateTimeOffset(2028, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var result = timing.GetNextOccurrence(mar2028, null!);
        // Beyond 2-year horizon → null
        Assert.Null(result);
    }

    [Fact]
    public void GetNextOccurrence_DayOfWeekWildcard_AnyDay()
    {
        var timing = new CronTiming("0 9 * * *"); // Any day at 9 AM

        var friday = new DateTimeOffset(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 6, 27, 9, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(friday, null!));
    }

    [Fact]
    public void GetNextOccurrence_PreciseMinute_NonZeroSeconds()
    {
        // When seconds are non-zero, we should still land on the next minute boundary
        var timing = new CronTiming("30 * * * *");

        var start = new DateTimeOffset(2026, 6, 25, 10, 30, 30, TimeSpan.Zero);
        // Already past 10:30, so next is 11:30
        Assert.Equal(new DateTimeOffset(2026, 6, 25, 11, 30, 0, TimeSpan.Zero), timing.GetNextOccurrence(start, null!));
    }

    [Fact]
    public void GetNextOccurrence_LastDayOfMonth_January()
    {
        var timing = new CronTiming("0 0 31 1 *"); // Jan 31 only

        var jan2026 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(jan2026, null!));

        var feb2026 = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        // Next Jan 31 is 2027
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero), timing.GetNextOccurrence(feb2026, null!));
    }

    [Fact]
    public void GetNextOccurrence_NoValidSlot_ReturnsNull()
    {
        // February 30 doesn't exist — this expression can never match
        var timing = new CronTiming("0 0 30 2 *");

        var start = new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
        var result = timing.GetNextOccurrence(start, null!);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("* * * * *", "cron")]
    [InlineData("0 0 * * *", "midnight")]
    [InlineData("*/5 * * * *", "five-min")]
    public void Constructor_StoresCustomNames(string expression, string name)
    {
        var timing = new CronTiming(expression, name);
        Assert.Equal(name, timing.TimingName);
    }
}
