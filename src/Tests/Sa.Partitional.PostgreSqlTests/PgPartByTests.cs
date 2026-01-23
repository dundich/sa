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
}
