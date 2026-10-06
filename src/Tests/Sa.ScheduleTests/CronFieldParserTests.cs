using Sa.Schedule.Cron;

namespace Sa.ScheduleTests;

/// <summary>
/// Regression tests for list elements that are full field shapes. Standard cron
/// allows a step ("0,*/15", "1-5/10", "5/10"), a range ("0-4,8-12") and "*"
/// inside a comma list; before the fix only bare numbers and L/W specials were
/// accepted as list elements, and a perfectly valid element such as "*/15" was
/// rejected with a misleading "Invalid value" error.
/// </summary>
public sealed class CronFieldParserTests
{
    [Theory]
    [InlineData("0,*/15", new[] { 0, 15, 30, 45 })]
    [InlineData("0,5-55/20", new[] { 0, 5, 25, 45 })]
    [InlineData("0,5/10", new[] { 0, 5, 15, 25, 35, 45, 55 })]
    [InlineData("0,1-5", new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData("0,*/15,15", new[] { 0, 15, 30, 45 })]
    public void Parse_ListElementsMayBeAnyFieldShape(string token, int[] expected)
    {
        var field = CronFieldParser.Parse(token, CronFieldKind.Minute);
        Assert.Equal(expected, field.Values);
    }

    [Fact]
    public void Parse_ListWithRangeStep_DayOfMonth()
    {
        // "1-5/10" over day-of-month: start 1, end 5, step 10 → only the 1st.
        var field = CronFieldParser.Parse("1-5/10", CronFieldKind.DayOfMonth);
        Assert.Equal([1], field.Values);
    }

    [Fact]
    public void Parse_ListWithStarStep_DayOfWeek_FoldsSevenIntoZero()
    {
        var field = CronFieldParser.Parse("0,*/2", CronFieldKind.DayOfWeek);

        // "*/2" over 0-7 (where 7 is Sunday == 0) → 0, 2, 4, 6.
        Assert.Equal([0, 2, 4, 6], field.Values);
    }

    [Fact]
    public void Parse_ListWithBareStar_IsTheFullRange()
    {
        // cronie tolerates "*" as a list element; it is the full field range.
        var field = CronFieldParser.Parse("1,*,31", CronFieldKind.DayOfMonth);
        Assert.Equal(31, field.Values.Length);
        Assert.Equal(1, field.Values[0]);
        Assert.Equal(31, field.Values[^1]);
    }

    [Theory]
    [InlineData("0,*/abc")]
    [InlineData("0,5-55/x")]
    [InlineData(",5")]
    [InlineData("0,")]
    public void Parse_ListWithMalformedElement_STillThrows(string token)
    {
        CronField act() => CronFieldParser.Parse(token, CronFieldKind.Minute);

        Assert.Throws<CronParseException>((Func<CronField>)act);
    }

    [Fact]
    public void Parse_ListWithSpecialStep_ThrowsTheStepSpecialMessage()
    {
        // "5L" is a special, and specials are not allowed in a step — the error
        // must come from the step parser, not the misleading "Invalid value".
        static CronField act() => CronFieldParser.Parse("0,5L/2", CronFieldKind.DayOfWeek);

        var ex = Assert.Throws<CronParseException>((Func<CronField>)act);
        Assert.DoesNotContain("Invalid value", ex.Message);
    }
}