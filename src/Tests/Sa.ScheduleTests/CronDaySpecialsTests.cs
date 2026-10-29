using System.Globalization;
using Sa.Schedule.Cron;

namespace Sa.ScheduleTests;

/// <summary>
/// Pins the Quartz-style day specials (<c>L</c>, <c>L-n</c>, <c>LW</c>, <c>dW</c>, <c>dL</c>),
/// the strict calendar check and the diagnostic data on <see cref="CronParseException"/>.
/// The occurrence table mirrors CRON.md, so changing either place has to be deliberate.
/// </summary>
public sealed class CronDaySpecialsTests
{
    private static DateTimeOffset At(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset Next(CronTiming timing, string from)
    {
        var next = timing.GetNextOccurrence(At(from));
        Assert.NotNull(next);
        return next.Value;
    }

    [Theory]
    [InlineData("0 0 L * *", "2026-06-25", "2026-06-30")]      // last day of June
    [InlineData("0 0 L-0 * *", "2026-06-25", "2026-06-30")]    // "L-0" is exactly "L"
    [InlineData("0 0 L-2 * *", "2026-06-25", "2026-06-28")]    // 30 - 2
    [InlineData("0 0 LW * *", "2026-06-25", "2026-06-30")]     // June ends on a Tuesday
    [InlineData("0 0 15W * *", "2026-06-01", "2026-06-15")]    // 15th is a Monday
    [InlineData("0 0 15W * *", "2026-07-20", "2026-08-14")]    // Aug 15 is a Saturday -> Friday 14
    [InlineData("0 0 15W * *", "2026-11-01", "2026-11-16")]    // Nov 15 is a Sunday -> Monday 16
    [InlineData("0 0 1W * *", "2026-07-31", "2026-08-03")]     // Saturday the 1st -> Monday the 3rd
    [InlineData("0 0 1W * *", "2026-01-31", "2026-02-02")]     // Sunday the 1st -> Monday the 2nd
    [InlineData("0 0 31W * *", "2026-01-01", "2026-01-30")]    // Saturday the 31st -> Friday 30
    [InlineData("0 0 31W * *", "2026-04-01", "2026-05-29")]    // April has no 31st; May 31 is a Sunday
    [InlineData("0 0 L-30 * *", "2025-12-31", "2026-01-01")]   // 31 - 30; shorter months never match
    [InlineData("0 0 29W 2 *", "2026-03-01", "2028-02-29")]    // 2027 February has only 28 days
    [InlineData("0 0 * * 5L", "2026-06-01", "2026-06-26")]     // last Friday of June
    [InlineData("0 0 * * 0L", "2026-06-01", "2026-06-28")]     // last Sunday of June
    [InlineData("0 0 * * 7L", "2026-06-01", "2026-06-28")]     // 7L folds into 0L
    public void GetNextOccurrence_DaySpecial(string expression, string from, string expected)
    {
        var timing = new CronTiming(expression);

        Assert.Equal(At(expected), Next(timing, from));
    }

    [Fact]
    public void LastDayOffset_ResolvesPerMonthLength()
    {
        var timing = new CronTiming("0 0 L-2 * *");

        Assert.Equal(At("2026-02-26"), Next(timing, "2026-01-31")); // 28-day February
        Assert.Equal(At("2028-02-27"), Next(timing, "2028-01-31")); // 29-day February
        Assert.Equal(At("2026-04-28"), Next(timing, "2026-03-31")); // 30-day April
        Assert.Equal(At("2026-07-29"), Next(timing, "2026-06-30")); // 31-day July
    }

    [Fact]
    public void LastWeekday_SkipsMonthsThatEndOnAWeekend()
    {
        // 2026 ends on a weekday only in March, April, June, July, August, September,
        // November and December; January, February, May and October end on a weekend and
        // are therefore skipped entirely (documented deviation from Quartz's "LW").
        var timing = new CronTiming("0 0 LW * *");
        var slots = new List<DateTimeOffset>();
        var cursor = At("2025-12-31");

        for (int i = 0; i < 8; i++)
        {
            var next = timing.GetNextOccurrence(cursor);
            Assert.NotNull(next);
            slots.Add(next.Value);
            cursor = next.Value;
        }

        Assert.Equal(
            new[]
            {
                "2026-03-31", "2026-04-30", "2026-06-30", "2026-07-31",
                "2026-08-31", "2026-09-30", "2026-11-30", "2026-12-31",
            }.Select(At).ToArray(),
            slots.ToArray());
    }

    [Fact]
    public void DaySpecial_CombinesWithDayOfWeek_UsingOrSemantics()
    {
        // Both day fields restricted -> either may match, specials included.
        var timing = new CronTiming("0 0 L * 1");

        Assert.Equal(At("2026-06-08"), Next(timing, "2026-06-02")); // Monday
        Assert.Equal(At("2026-06-29"), Next(timing, "2026-06-27")); // Monday
        Assert.Equal(At("2026-06-30"), Next(timing, "2026-06-29")); // last day of June
    }

    [Theory]
    [InlineData("0 0 15W,L * *", "cannot be combined")]
    [InlineData("0 0 L-31 * *", "between 0 and 30")]
    [InlineData("0 0 32W * *", "out of range")]
    [InlineData("0 0 * * 8L", "out of range")]
    [InlineData("0 0 * * L", "Invalid cron field")]
    [InlineData("0 0 5L/2 * *", "cannot be used in a step")]
    public void MalformedDaySpecial_Throws(string expression, string message)
    {
        var ex = Assert.Throws<CronParseException>(() => new CronTiming(expression));

        Assert.Contains(message, ex.Message);
        Assert.False(string.IsNullOrEmpty(ex.Field));
        Assert.False(string.IsNullOrEmpty(ex.Token));
    }

    [Theory]
    [InlineData("0 0 31 4 *")]      // April 31
    [InlineData("0 0 30 2 *")]      // February 30
    [InlineData("0 0 31 4 */1")]    // the wildcard spelled as a full-range value
    [InlineData("0 0 31W 2 *")]     // no 31st in February
    [InlineData("0 0 L-30 2 *")]    // 30 days before a 28/29-day month end
    public void Strict_RejectsExpressionsThatCanNeverFire(string expression)
    {
        var ex = Assert.Throws<CronParseException>(() => CronTiming.Strict(expression));

        Assert.Equal("day-of-month", ex.Field);
    }

    [Theory]
    [InlineData("0 0 29 2 *")]      // leap years are inside the 28-year horizon
    [InlineData("0 0 31 * *")]      // some months have 31 days
    [InlineData("0 0 L 2 *")]
    [InlineData("0 0 29W 2 *")]
    [InlineData("0 0 1W 2 *")]
    [InlineData("0 0 L-2 2 *")]     // 28 - 2 is a real February day
    [InlineData("0 0 31 4 1")]      // OR with Monday always fires
    public void Strict_AcceptsReachableExpressions(string expression)
    {
        _ = CronTiming.Strict(expression);
    }

    [Theory]
    [InlineData("0 0 31 4 *")]
    [InlineData("0 0 30 2 *")]
    [InlineData("0 0 31W 2 *")]
    public void LenientCtor_ReturnsNullInsteadOfThrowing(string expression)
    {
        var timing = new CronTiming(expression);

        Assert.Null(timing.GetNextOccurrence(At("2026-06-25")));
    }

    [Theory]
    [InlineData("60 * * * *", "minute", "60")]
    [InlineData("* 24 * * *", "hour", "24")]
    [InlineData("* * 32 * *", "day-of-month", "32")]
    [InlineData("* * * 13 *", "month", "13")]
    [InlineData("* * * * 8", "day-of-week", "8")]
    [InlineData("* *", "expression", "* *")]
    public void CronParseException_NameFieldAndToken(string expression, string field, string token)
    {
        var ex = Assert.Throws<CronParseException>(() => new CronTiming(expression));

        Assert.Equal(field, ex.Field);
        Assert.Equal(token, ex.Token);
    }

    [Fact]
    public void Expression_IsTheRawInput_AndWhitespaceIsTolerant()
    {
        var timing = new CronTiming("  0\t9   *  * *  ");

        Assert.Equal("  0\t9   *  * *  ", timing.Expression);
        Assert.Equal(At("2026-06-25T09:00:00Z"), Next(timing, "2026-06-25T08:00:00Z"));
    }
}
