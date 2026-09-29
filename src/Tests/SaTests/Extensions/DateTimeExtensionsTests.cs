using Sa.Extensions;

namespace SaTests.Extensions;

/// <summary>
/// Регрессия на <c>ToUnixTimestamp</c>: приведение к <c>long</c> округляет к нулю, а для дат
/// до 1970 это округление вверх, к эпохе.
/// </summary>
public class DateTimeExtensionsTests
{
    [Theory]
    [InlineData(2024, 1, 1, 0, 0, 0, 0, 1704067200L)]
    [InlineData(1970, 1, 1, 0, 0, 1, 0, 1L)]
    [InlineData(1969, 12, 31, 23, 59, 59, 0, -1L)]
    [InlineData(1969, 12, 31, 23, 59, 58, 500, -2L)]   // было 0 — усечение к эпохе
    [InlineData(1969, 12, 31, 23, 59, 59, 500, -1L)]   // было 0
    public void ToUnixTimestamp_BeforeEpoch_FloorsInsteadOfTruncatingTowardsZero(
        int y, int mo, int d, int h, int mi, int s, int ms, long expected)
    {
        // Arrange
        var date = new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Utc);

        // Act
        var result = date.ToUnixTimestamp();

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToUnixTimestamp_Milliseconds_FloorsInsteadOfTruncatingTowardsZero()
    {
        // Arrange
        var date = new DateTime(1969, 12, 31, 23, 59, 59, 500, DateTimeKind.Utc);

        // Act
        var result = date.ToUnixTimestamp(isInMilliseconds: true);

        // Assert — усечение к нулю давало 0
        Assert.Equal(-500L, result);
    }

    [Theory]
    [InlineData(1970, 1, 1, 0, 0, 0, 0, 0L)]
    [InlineData(2024, 1, 1, 0, 0, 0, 0, 1704067200L)]
    [InlineData(2024, 1, 1, 0, 0, 0, 999, 1704067200L)]
    [InlineData(2024, 1, 1, 0, 0, 1, 0, 1704067201L)]   // 1704067200 + 1 с
    public void ToUnixTimestamp_AfterEpoch_MatchesTruncation(
        int y, int mo, int d, int h, int mi, int s, int ms, long expected)
    {
        // После эпохи округление вниз и усечение совпадают — поведение не изменилось.
        var date = new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Utc);

        Assert.Equal(expected, date.ToUnixTimestamp());
    }

    [Fact]
    public void ToUnixTimestamp_RoundTrip_KeepsTheInstantForSecondPrecision()
    {
        var date = new DateTime(2024, 6, 15, 12, 30, 45, DateTimeKind.Utc);

        var roundTripped = date.ToUnixTimestamp().ToDateTimeFromUnixTimestamp();

        Assert.Equal(date, roundTripped);
        Assert.Equal(DateTimeKind.Utc, roundTripped.Kind);
    }

    [Fact]
    public void ToUnixTimestamp_MinValue_ProducesNegativeStamp()
    {
        var result = DateTime.MinValue.ToUnixTimestamp();

        Assert.Equal(-62135596800L, result);
    }
}
