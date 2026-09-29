using Sa.Extensions;

namespace SaTests.Extensions;

/// <summary>
/// Регрессия на преобразование чисел в дату: <c>ulong</c> переполнялся через
/// <c>(long)</c>, а <c>double</c> обрезался к нулю, а не вниз.
/// </summary>
public class NumericExtensionsTests
{
    [Fact]
    public void ToDateTimeFromUnixTimestamp_ulongMaxValue_ThrowsInsteadOfWrapping()
    {
        // Arrange
        // (long)ulong.MaxValue == -1 в unchecked, поэтому результат был 1969-12-31T23:59:59Z
        // на любой входной строке, которая не помещалась в long.

        // Act
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ulong.MaxValue.ToDateTimeFromUnixTimestamp());

        // Assert
        Assert.Equal("timestamp", ex.ParamName);
    }

    [Fact]
    public void ToDateTimeFromUnixTimestamp_ulongAboveLongRange_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ((ulong)long.MaxValue + 1).ToDateTimeFromUnixTimestamp());

        Assert.Equal("timestamp", ex.ParamName);
    }

    [Fact]
    public void ToDateTimeFromUnixTimestamp_ulongAtLongMaxValue_IsAccepted()
    {
        // Граница проходит: значение, помещающееся в long, обрабатывается как раньше —
        // до DateTime не хватает диапазона, и это сообщает сам AddSeconds.
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ulong)long.MaxValue).ToDateTimeFromUnixTimestamp());
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(1000),
            ((ulong)1000).ToDateTimeFromUnixTimestamp());
    }

    [Theory]
    [InlineData(0UL, 1970, 1, 1, 0, 0, 0)]
    [InlineData(1UL, 1970, 1, 1, 0, 0, 1)]
    [InlineData(1704067200UL, 2024, 1, 1, 0, 0, 0)]
    public void ToDateTimeFromUnixTimestamp_ulongWithinLongRange_Converts(ulong input, int y, int mo, int d, int h, int mi, int s)
    {
        var result = input.ToDateTimeFromUnixTimestamp();

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), result);
    }

    [Fact]
    public void ToDateTimeFromUnixTimestamp_NegativeSubSecond_FloorsInsteadOfTruncatingTowardsZero()
    {
        // (long)(-0.5) == 0, то есть 1969-12-31T23:59:59.5Z превращался в саму эпоху —
        // округление в сторону эпохи на полсекунды вперёд.
        var result = ((double)-0.5).ToDateTimeFromUnixTimestamp();

        Assert.Equal(new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData(-1.0, 1969, 12, 31, 23, 59, 59)]
    [InlineData(-1.5, 1969, 12, 31, 23, 59, 58)]
    [InlineData(-62135596800.0, 1, 1, 1, 0, 0, 0)]  // DateTime.MinValue в секундах
    public void ToDateTimeFromUnixTimestamp_NegativeDouble_FloorsToSecondBoundary(
        double input, int y, int mo, int d, int h, int mi, int s)
    {
        var result = input.ToDateTimeFromUnixTimestamp();

        Assert.Equal(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), result);
    }

    [Theory]
    [InlineData(0.5, 0)]   // усечение к нулю
    [InlineData(1.9, 1)]
    [InlineData(1704067200.5, 1704067200)]
    public void ToDateTimeFromUnixTimestamp_PositiveSubSecond_TruncatesToSecondBoundary(double input, long expectedSeconds)
    {
        // Для значений после эпохи округление вниз и усечение совпадают.
        var result = input.ToDateTimeFromUnixTimestamp();

        Assert.Equal(DateTime.UnixEpoch.AddSeconds(expectedSeconds), result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ToDateTimeFromUnixTimestamp_NonFinite_ThrowsArgumentOutOfRangeException(double input)
    {
        // Раньше (long)double.NaN давал long.MinValue (зависело от платформы) и падение
        // уже внутри AddSeconds, то есть исключение не соответствовало входу.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => input.ToDateTimeFromUnixTimestamp());

        Assert.Equal("timestamp", ex.ParamName);
    }

    [Fact]
    public void ToDateTimeFromUnixTimestamp_NullableOverloads_PassThrough()
    {
        Assert.Null(((long?)null).ToDateTimeFromUnixTimestamp());
        Assert.Null(((ulong?)null).ToDateTimeFromUnixTimestamp());
        Assert.Null(((double?)null).ToDateTimeFromUnixTimestamp());
        Assert.Null(((string)null!).ToDateTimeFromUnixTimestamp());
        Assert.Null("не число".ToDateTimeFromUnixTimestamp());

        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(5),
            ((long?)5).ToDateTimeFromUnixTimestamp());
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(5),
            ((ulong?)5).ToDateTimeFromUnixTimestamp());
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(5),
            ((double?)5).ToDateTimeFromUnixTimestamp());
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(5),
            "5".ToDateTimeFromUnixTimestamp());
    }
}
