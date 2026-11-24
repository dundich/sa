using Sa.Outbox.PostgreSql.Tracking;

namespace Sa.Outbox.PostgreSqlTests.Tracking;

/// <summary>
/// Unit tests (no database) for the data window every tracking and maintenance call resolves.
/// </summary>
public class TrackingWindowTests
{
    [Fact]
    public void Resolve_WithoutBounds_UsesTheSevenDayLookback()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        (DateTimeOffset from, DateTimeOffset to) = TrackingWindow.Resolve(TimeProvider.System, null, null);

        Assert.True(to >= before);
        Assert.Equal(TrackingWindow.DefaultLookback, to - from);
    }

    [Fact]
    public void Resolve_BlamesTheBoundTheCallerSupplied()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // No `toDate`: the window ends at "now", so a future `fromDate` is the mistake. Naming
        // `toDate` here would blame a parameter the caller never passed.
        ArgumentOutOfRangeException future = Assert.Throws<ArgumentOutOfRangeException>(
            () => TrackingWindow.Resolve(TimeProvider.System, fromDate: now.AddDays(1), toDate: null));

        Assert.Equal("fromDate", future.ParamName);

        ArgumentOutOfRangeException inverted = Assert.Throws<ArgumentOutOfRangeException>(
            () => TrackingWindow.Resolve(TimeProvider.System, now.AddDays(-1), now.AddDays(-2)));

        Assert.Equal("toDate", inverted.ParamName);
    }

    [Fact]
    public void Floor_WithPlentyOfRoom_SubtractsAndKeepsTheOffset()
    {
        DateTimeOffset value = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(3));

        DateTimeOffset floored = TrackingWindow.Floor(value, TimeSpan.FromDays(7));

        Assert.Equal(value - TimeSpan.FromDays(7), floored);
        Assert.Equal(value.Offset, floored.Offset);
    }

    [Fact]
    public void Floor_NearMinValue_SaturatesInsteadOfThrowing()
    {
        // `DateTimeOffset.Ticks` is the clock reading, and adding ticks re-validates the UTC range: a
        // positive offset four hours short of a full week into year 1 used to throw even though the
        // clock part stayed in range.
        DateTimeOffset value = DateTimeOffset.MinValue
            .AddDays(7)
            .AddHours(-4)
            .ToOffset(TimeSpan.FromHours(14));

        Assert.Equal(DateTimeOffset.MinValue, TrackingWindow.Floor(value, TimeSpan.FromDays(7)));
        Assert.Equal(DateTimeOffset.MinValue, TrackingWindow.Floor(value, TimeSpan.FromDays(30)));
    }
}
