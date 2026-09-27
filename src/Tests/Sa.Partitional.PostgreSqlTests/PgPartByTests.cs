using Sa.Classes;
using Sa.Partitional.PostgreSql;

namespace Sa.Partitional.PostgreSqlTests;

public class PgPartByTests
{

    [Fact]
    public void Day_Fmt_ReturnsCorrectFormat()
    {
        // Arrange
        var testDate = new DateTimeOffset(2023, 12, 25, 14, 30, 23, TimeSpan.Zero);

        // Act
        var name = PgPartBy.Day.Fmt(testDate);

        // Assert
        Assert.Equal("y2023m12d25", name);
    }

    [Theory]
    [InlineData("y2023m12d25", 2023, 12, 25)]
    [InlineData("y2024m01d01", 2024, 1, 1)]
    [InlineData("_outbox_root__y2021m12d30", 2021, 12, 30)]
    public void Day_ParseFmt_ValidStrings(string input, int expectedYear, int expectedMonth, int expectedDay)
    {

        // Act
        var result = PgPartBy.Day.ParseFmt(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedYear, result.Value.Year);
        Assert.Equal(expectedMonth, result.Value.Month);
        Assert.Equal(expectedDay, result.Value.Day);
        Assert.Equal(0, result.Value.Hour);
        Assert.Equal(0, result.Value.Minute);
        Assert.Equal(0, result.Value.Second);
        Assert.Equal(TimeSpan.Zero, result.Value.Offset);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("y2023m13d32")]
    [InlineData("")]
    public void Day_ParseFmt_ReturnsNull_ForInvalidStrings(string input)
    {
        try
        {
            // Act
            var result = PgPartBy.Day.ParseFmt(input);

            // Assert
            Assert.Null(result);
        }
        catch
        {
            Assert.True(true);
        }
    }


    [Fact]
    public void Month_Fmt_ReturnsCorrectFormat()
    {
        // Arrange
        var testDate = new DateTimeOffset(2023, 12, 25, 14, 30, 23, TimeSpan.Zero);

        // Act
        var name = PgPartBy.Month.Fmt(testDate);

        // Assert
        Assert.Equal("y2023m12", name);
    }

    [Theory]
    [InlineData("y2023m12", 2023, 12)]
    [InlineData("y2024m01", 2024, 1)]
    [InlineData("_outbox_root__y2021m06", 2021, 6)]
    public void Month_ParseFmt_ValidStrings(string input, int expectedYear, int expectedMonth)
    {

        // Act
        var result = PgPartBy.Month.ParseFmt(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedYear, result.Value.Year);
        Assert.Equal(expectedMonth, result.Value.Month);
        Assert.Equal(1, result.Value.Day);
        Assert.Equal(0, result.Value.Hour);
        Assert.Equal(0, result.Value.Minute);
        Assert.Equal(0, result.Value.Second);
        Assert.Equal(TimeSpan.Zero, result.Value.Offset);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("y2023m13")]
    [InlineData("")]
    public void Month_ParseFmt_ReturnsNull_ForInvalidStrings(string input)
    {
        try
        {
            // Act
            var result = PgPartBy.Month.ParseFmt(input);

            // Assert
            Assert.Null(result);
        }
        catch
        {
            Assert.True(true);
        }
    }

    [Fact]
    public void Year_Fmt_ReturnsCorrectFormat()
    {
        // Arrange
        var testDate = new DateTimeOffset(2023, 12, 25, 14, 30, 23, TimeSpan.Zero);

        // Act
        var name = PgPartBy.Year.Fmt(testDate);

        // Assert
        Assert.Equal("y2023", name);
    }

    [Theory]
    [InlineData("y2023", 2023)]
    [InlineData("y2024", 2024)]
    [InlineData("_outbox_root__y2021", 2021)]
    public void Year_Parse_FmtValidStrings(string input, int expectedYear)
    {

        // Act
        var result = PgPartBy.Year.ParseFmt(input);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedYear, result.Value.Year);
        Assert.Equal(1, result.Value.Month);
        Assert.Equal(1, result.Value.Day);
        Assert.Equal(0, result.Value.Hour);
        Assert.Equal(0, result.Value.Minute);
        Assert.Equal(0, result.Value.Second);
        Assert.Equal(TimeSpan.Zero, result.Value.Offset);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("y202")]
    [InlineData("")]
    public void Year_ParseFmt_ReturnsNull_ForInvalidStrings(string input)
    {
        try
        {
            // Act
            var result = PgPartBy.Year.ParseFmt(input);

            // Assert
            Assert.Null(result);
        }
        catch
        {
            Assert.True(true);
        }
    }

    [Fact]
    public void Day_Fmt_UsesTheUtcCalendarDay_NotTheCallersOffset()
    {
        // 2026-09-27T02:30+05:00 is 2026-09-26T21:30Z - a different calendar day, and the
        // partitioning range (which is computed in UTC) belongs to the 26th.
        DateTimeOffset shifted = new(2026, 9, 27, 2, 30, 0, TimeSpan.FromHours(5));

        Assert.Equal("y2026m09d26", PgPartBy.Day.Fmt(shifted));
    }

    [Fact]
    public void Month_Fmt_UsesTheUtcCalendarMonth_NotTheCallersOffset()
    {
        // 2026-10-01T01:00+05:00 is 2026-09-30T20:00Z.
        DateTimeOffset shifted = new(2026, 10, 1, 1, 0, 0, TimeSpan.FromHours(5));

        Assert.Equal("y2026m09", PgPartBy.Month.Fmt(shifted));
    }

    [Fact]
    public void Year_Fmt_UsesTheUtcCalendarYear_NotTheCallersOffset()
    {
        // 2026-01-01T01:00+05:00 is 2025-12-31T20:00Z.
        DateTimeOffset shifted = new(2026, 1, 1, 1, 0, 0, TimeSpan.FromHours(5));

        Assert.Equal("y2025", PgPartBy.Year.Fmt(shifted));
    }

    [Fact]
    public void Fmt_NamesThePartitionThatGetRangeBounds()
    {
        // The name and the range have to agree: a partition named after the local day while bounded
        // by the UTC day would leave the instant outside the partition it was written to.
        DateTimeOffset shifted = new(2026, 9, 27, 2, 30, 0, TimeSpan.FromHours(5));

        foreach (PgPartBy partBy in new[] { PgPartBy.Day, PgPartBy.Month, PgPartBy.Year })
        {
            Section<DateTimeOffset> range = partBy.GetRange(shifted);
            string name = partBy.Fmt(shifted);

            Assert.Equal(partBy.Fmt(range.Start), name);
            Assert.Equal(partBy.Fmt(range.End.AddTicks(-1)), name);
            Assert.Equal(TimeSpan.Zero, range.Start.Offset);
            Assert.True(range.InRange(shifted));
        }
    }

    [Fact]
    public void Fmt_And_ParseFmt_RoundTrip_ForANonUtcInstant()
    {
        DateTimeOffset shifted = new(2026, 9, 27, 2, 30, 0, TimeSpan.FromHours(5));

        foreach (PgPartBy partBy in new[] { PgPartBy.Day, PgPartBy.Month, PgPartBy.Year })
        {
            string name = partBy.Fmt(shifted);
            DateTimeOffset? parsed = partBy.ParseFmt(name);

            Assert.NotNull(parsed);
            Assert.Equal(name, partBy.Fmt(parsed.Value));
            Assert.Equal(partBy.GetRange(shifted).Start, parsed.Value);
        }
    }

    [Fact]
    public void Fmt_And_ParseFmt_Agree_ForEveryUtcInstantOfAPartition()
    {
        // Whichever instant of the day is handed in, the partition resolves to the same range.
        DateTimeOffset start = PgPartBy.Day.GetRange(DateTimeOffset.UnixEpoch).Start;

        for (int hour = 0; hour < 24; hour++)
        {
            DateTimeOffset instant = start.AddHours(hour);
            DateTimeOffset? parsed = PgPartBy.Day.ParseFmt(PgPartBy.Day.Fmt(instant));

            Assert.NotNull(parsed);
            Assert.Equal(start, parsed.Value);
            Assert.Equal(PgPartBy.Day.Fmt(start), PgPartBy.Day.Fmt(instant));
        }
    }

    [Fact]
    public void FromPartName_FallsBackToRoot_ForNullAndUnknownNames()
    {
        // Only the root partition is a registered Part, so every other name - including the
        // PgPartBy names such as "month" - resolves to the root partition, which is day-based.
        Assert.Equal(PgPartBy.Day, PgPartBy.FromPartName(null));
        Assert.Equal(PgPartBy.Day, PgPartBy.FromPartName("nonsense"));
        Assert.Equal(PgPartBy.Day, PgPartBy.FromPartName("month"));
        Assert.Equal(PgPartBy.Day, PgPartBy.FromPartName(Part.RootId));
    }
}
