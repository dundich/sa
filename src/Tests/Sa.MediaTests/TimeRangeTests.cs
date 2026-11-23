using Sa.Media;

namespace Sa.MediaTests;

/// <summary>
/// Фабрики и вычисляемые свойства <see cref="TimeRange"/> плюс <see cref="TimeRange.Validate"/>:
/// диапазон — публичный тип, но раньше не покрывался ни одним тестом, хотя на нём держится
/// разметка временных границ.
/// </summary>
public sealed class TimeRangeTests
{
    [Fact]
    public void Create_SetsBothBounds()
    {
        var range = TimeRange.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(1), range.From);
        Assert.Equal(TimeSpan.FromSeconds(2), range.To);
        Assert.True(range.HasEnd);
        Assert.Equal(TimeSpan.FromSeconds(1), range.Duration);
        Assert.True(range.IsPositive);
        Assert.Equal(1.0, range.FromSeconds, 9);
        Assert.Equal(2.0, range.ToSeconds, 9);
    }

    [Fact]
    public void Default_IsOpenEndedFromZero()
    {
        Assert.Equal(TimeSpan.Zero, TimeRange.Default.From);
        Assert.Equal(TimeSpan.MaxValue, TimeRange.Default.To);
        Assert.False(TimeRange.Default.HasEnd);
    }

    [Fact]
    public void Ms_UsesMillisecondBounds()
    {
        TimeRange range = TimeRange.Ms(250, 1750);

        Assert.Equal(250, range.From.TotalMilliseconds, 6);
        Assert.Equal(1750, range.To.TotalMilliseconds, 6);
    }

    [Fact]
    public void Ms_LongMaxValueMeansOpenEnded()
    {
        TimeRange range = TimeRange.Ms(0, long.MaxValue);

        Assert.Equal(TimeSpan.Zero, range.From);
        Assert.Equal(TimeSpan.MaxValue, range.To);
        Assert.False(range.HasEnd);
    }

    [Fact]
    public void Seconds_WithoutEnd_IsOpenEnded()
    {
        TimeRange range = TimeRange.Seconds(5);

        Assert.Equal(5.0, range.FromSeconds, 9);
        Assert.Equal(TimeSpan.MaxValue, range.To);
    }

    [Fact]
    public void Seconds_WithUnrepresentableEnd_FallsBackToOpenEnded()
    {
        // double.MaxValue не помещается в TimeSpan: конечное значение превратилось бы
        // в исключение, поэтому диапазон остаётся открытым.
        TimeRange range = TimeRange.Seconds(5, double.MaxValue);

        Assert.Equal(TimeSpan.MaxValue, range.To);
        Assert.False(range.HasEnd);
    }

    [Fact]
    public void RangeFromDuration_AddsTheDurationToTheStart()
    {
        TimeRange range = TimeRange.RangeFromDuration(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500));

        Assert.Equal(2.0, range.FromSeconds, 9);
        Assert.Equal(2.5, range.ToSeconds, 9);
        Assert.Equal(TimeSpan.FromMilliseconds(500), range.Duration);
    }

    [Fact]
    public void Duration_OfInvertedRange_IsNegativeAndNotPositive()
    {
        var range = TimeRange.Create(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(-1), range.Duration);
        Assert.False(range.IsPositive);
    }

    [Fact]
    public void Validate_AcceptsAValidRange()
    {
        TimeRange.Create(TimeSpan.Zero, TimeSpan.FromSeconds(1)).Validate();
        TimeRange.Default.Validate();
    }

    [Fact]
    public void Validate_NegativeStart_Throws()
    {
        var range = TimeRange.Create(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(range.Validate);

        Assert.Equal("From", exception.ParamName);
    }

    [Fact]
    public void Validate_EndBeforeStart_Throws()
    {
        var range = TimeRange.Create(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(range.Validate);

        Assert.Equal("To", exception.ParamName);
    }

    [Fact]
    public void Validate_ZeroLengthRange_IsValid()
    {
        TimeRange.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)).Validate();
    }
}
