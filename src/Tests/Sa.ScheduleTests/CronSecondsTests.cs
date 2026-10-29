using System.Globalization;
using Sa.Schedule.Cron;

namespace Sa.ScheduleTests;

/// <summary>
/// The optional leading seconds field: a 6-field expression is detected by the field count
/// ("second minute hour day-of-month month day-of-week") and searched at second resolution,
/// while the 5-field form keeps its minute-precise behaviour (it only ever fires on second 0).
/// </summary>
public sealed class CronSecondsTests
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
    [InlineData("* * * * * *", "2026-06-25T10:30:00Z", "2026-06-25T10:30:01Z")]      // every second
    [InlineData("* * * * * *", "2026-06-25T10:30:00.500Z", "2026-06-25T10:30:01Z")]  // exclusive, sub-second
    [InlineData("*/15 * * * * *", "2026-06-25T10:30:00Z", "2026-06-25T10:30:15Z")]   // every 15 seconds
    [InlineData("30 * * * * *", "2026-06-25T10:30:00Z", "2026-06-25T10:30:30Z")]     // second 30 of every minute
    [InlineData("30 * * * * *", "2026-06-25T10:30:30Z", "2026-06-25T10:31:30Z")]     // ...and the next one
    [InlineData("0,30 */5 * * * *", "2026-06-25T10:00:00Z", "2026-06-25T10:00:30Z")] // list of seconds
    [InlineData("59 59 9 * * *", "2026-06-25T09:58:59Z", "2026-06-25T09:59:59Z")]    // minute rollover
    [InlineData("0 0 9 * * *", "2026-06-25T08:59:59Z", "2026-06-25T09:00:00Z")]     // second-aligned hour
    [InlineData("0 0 9 * * *", "2026-06-25T10:00:00Z", "2026-06-26T09:00:00Z")]     // ...and the next day
    [InlineData("0 0 0 L * *", "2026-06-25T00:00:00Z", "2026-06-30T00:00:00Z")]     // specials still work
    public void SixFieldExpression_FiresOnSeconds(string expression, string from, string expected)
    {
        var timing = new CronTiming(expression);

        Assert.Equal(At(expected), Next(timing, from));
    }

    [Theory]
    [InlineData("5-10/2 0 9 * * 1-5", "2026-06-26T09:00:00Z", "2026-06-26T09:00:05Z")] // Fri, seconds 5,7,9
    [InlineData("5-10/2 0 9 * * 1-5", "2026-06-26T09:00:05Z", "2026-06-26T09:00:07Z")]
    [InlineData("5-10/2 0 9 * * 1-5", "2026-06-26T09:00:09Z", "2026-06-29T09:00:05Z")] // weekend skipped
    public void SixFieldExpression_KeepsDaySemantics(string expression, string from, string expected)
    {
        var timing = new CronTiming(expression);

        Assert.Equal(At(expected), Next(timing, from));
    }

    [Fact]
    public void SixFieldExpression_CrossesTheYearBoundary()
    {
        var timing = new CronTiming("59 59 23 31 12 *");

        Assert.Equal(At("2026-12-31T23:59:59Z"), Next(timing, "2026-06-25T00:00:00Z"));
        Assert.Equal(At("2027-12-31T23:59:59Z"), Next(timing, "2026-12-31T23:59:59Z"));
    }

    [Theory]
    [InlineData("* * * * *", "2026-06-25T10:30:45.500Z", "2026-06-25T10:31:00Z")] // minute precision
    [InlineData("30 * * * *", "2026-06-25T10:29:59.999Z", "2026-06-25T10:30:00Z")]
    public void FiveFieldExpression_StaysMinutePrecise(string expression, string from, string expected)
    {
        var timing = new CronTiming(expression);

        var next = Next(timing, from);

        Assert.Equal(At(expected), next);
        Assert.Equal(0, next.Second);
    }

    [Fact]
    public void SixFieldExpression_FormatsAreIndependent()
    {
        // The same textual tail means different things: "0 9 * * *" is 09:00 daily,
        // "0 0 9 * * *" is also 09:00 daily, but "0 9 * * * *" is every second 0 of minute 9.
        Assert.Equal(At("2026-06-26T09:00:00Z"), Next(new CronTiming("0 9 * * *"), "2026-06-25T10:00:00Z"));
        Assert.Equal(At("2026-06-26T09:00:00Z"), Next(new CronTiming("0 0 9 * * *"), "2026-06-25T10:00:00Z"));
        Assert.Equal(At("2026-06-25T10:09:00Z"), Next(new CronTiming("0 9 * * * *"), "2026-06-25T10:00:00Z"));
    }

    [Theory]
    [InlineData("60 * * * * *", "second", "60")]
    [InlineData("*/0 * * * * *", "second", "0")]
    [InlineData("L * * * * *", "second", "L")]
    [InlineData("? * * * * *", "second", "?")]
    [InlineData("* * * * * * *", "expression", "* * * * * * *")] // 7 fields — no year field
    public void MalformedSixFieldExpression_Throws(string expression, string field, string token)
    {
        var ex = Assert.Throws<CronParseException>(() => new CronTiming(expression));

        Assert.Equal(field, ex.Field);
        Assert.Equal(token, ex.Token);
    }

    [Theory]
    [InlineData("0 0 0 31 4 *")] // 00:00:00 on April 31
    [InlineData("0 0 0 30 2 *")]
    public void Strict_RejectsSixFieldExpressionsThatCanNeverFire(string expression)
    {
        var ex = Assert.Throws<CronParseException>(() => CronTiming.Strict(expression));

        Assert.Equal("day-of-month", ex.Field);
    }

    [Theory]
    [InlineData("0 0 0 29 2 *")]
    [InlineData("*/30 * * * * *")]
    [InlineData("0 0 0 L * *")]
    public void Strict_AcceptsReachableSixFieldExpressions(string expression)
    {
        _ = CronTiming.Strict(expression);
    }

    [Fact]
    public void LenientSixFieldCtor_ReturnsNullInsteadOfThrowing()
    {
        var timing = new CronTiming("0 0 0 31 4 *");

        Assert.Null(timing.GetNextOccurrence(At("2026-06-25T00:00:00Z")));
    }

    [Fact]
    public void SixFieldExpression_RawTextIsPreserved_AndWhitespaceIsTolerant()
    {
        var timing = new CronTiming("  0\t*/5  *  * * *  ");

        Assert.Equal("  0\t*/5  *  * * *  ", timing.Expression);
        Assert.Equal(At("2026-06-25T10:00:00Z"), Next(timing, "2026-06-25T09:59:59Z"));
    }
}
