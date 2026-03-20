using Sa.Media;

namespace Sa.MediaTests;

public class TimeRangeExpanderTests
{
    #region Helper Methods

    private static TimeRange Ms(long from, long to) => TimeRange.Ms(from, to);

    private static TimeRange[][] ToChunks(params TimeRange[][] chunks) => chunks;

    private static void AssertTimeRangesEqual(TimeRange expected, TimeRange actual)
    {
        Assert.NotNull(actual);
        Assert.NotNull(expected);

        Assert.Equal(
            expected.From.TotalMilliseconds,
            actual.From.TotalMilliseconds,
            precision: 0);

        Assert.Equal(
            expected.To.TotalMilliseconds,
            actual.To.TotalMilliseconds,
            precision: 0);
    }

    private static void AssertChunksEqual(TimeRange[][] expected, TimeRange[][] actual)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Length, actual[i].Length);

            for (int j = 0; j < expected[i].Length; j++)
            {
                AssertTimeRangesEqual(expected[i][j], actual[i][j]);
            }
        }
    }

    private static void AssertApproxEqual(long expected, long actual, long tolerance = 1)
    {
        var diff = Math.Abs(expected - actual);
        Assert.True(diff <= tolerance);
    }

    #endregion



    [Fact]
    public void ExpandTimeRanges_BasicExpansion_WithPositiveDeltas()
    {
        // Arrange
        var input = ToChunks(
            [
                Ms(800, 1900),
                Ms(5000, 5800),
                Ms(7200, 8700),
                Ms(9000, 9700)
            ],
            [
                Ms(2800, 4600),
                Ms(6300, 6899),
                Ms(10300, 10700)
            ]
        );

        var expected = ToChunks(
            [
                Ms(300, 2350),    // 800-500, (2800-1900)/2+1900
                Ms(4800, 6050),   // 5000-(5000-4600)/2, ...
                Ms(7050, 10000),  // mergе + delta
            ],
            [
                Ms(2350, 4800),
                Ms(6050, 7049),
                Ms(10000, 11200)
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(
            input,
            thresholdMillesecods: 500,
            gapMilliseconds: 0);

        // Assert
        AssertChunksEqual(expected, actual);
    }



    [Fact]
    public void ExpandTimeRanges_MergeCloseRanges_WithinChunk()
    {
        // Arrange
        var input = ToChunks(
            [
                Ms(1000, 2000),
                Ms(2300, 3000),   // thresholdMillesecods = 300 < 500 → сольётся с предыдущим
                Ms(5000, 6000)
            ],
            [
                Ms(3500, 4000)
            ]
        );

        var expected = ToChunks(
            [
                Ms(500, 3250),    // объединённый
                Ms(4500, 6500)    // расширение
            ],
            [
                Ms(3250, 4500)    // расширение
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }



    [Fact]
    public void ExpandTimeRanges_EmptyInput_ReturnsEmpty()
    {
        // Arrange
        var input = ToChunks(
            [],
            []
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.Equal(2, actual.Length);
        Assert.Empty(actual[0]);
        Assert.Empty(actual[1]);
    }




    [Fact]
    public void ExpandTimeRanges_SingleRange_ExpandsBothSides()
    {
        // Arrange
        var input = ToChunks(
            [Ms(5000, 6000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        Assert.Single(actual);
        Assert.Single(actual[0]);

        Assert.True(actual[0][0].From.TotalMilliseconds <= 5000);
        Assert.True(actual[0][0].To.TotalMilliseconds >= 6000);
    }


    [Fact]
    public void ExpandTimeRanges_SufficientThreshold_ExpandsFully()
    {
        // Arrange
        var input = ToChunks(
            [
                Ms(0, 1000),
                Ms(2000, long.MaxValue)
            ]
        );

        var expected = ToChunks(
            [
                Ms(0, 1500),
                Ms(1500, (long)TimeSpan.MaxValue.TotalMilliseconds)
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }


    [Fact]
    public void ExpandTimeRanges_CloseRanges_DifferentChunks_NoMerge()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 2000)],
            [Ms(2100, 3000)] // no merge
        );

        var expected = ToChunks(
            [Ms(500, 2050)],
            [Ms(2050, 3500)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }


    [Fact]
    public void ExpandTimeRanges_WithGap_DifferentChunks_NoMerge()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 2000)],
            [Ms(2100, 3000)]
        );

        var expected = ToChunks(
            [Ms(500, 2025)],
            [Ms(2075, 3500)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(
            input, thresholdMillesecods: 500,
            gapMilliseconds: 50);

        // Assert
        AssertChunksEqual(expected, actual);

        Assert.Equal(50, actual[1][0].From.TotalMilliseconds - actual[0][0].To.TotalMilliseconds, 1);
    }


    [Fact]
    public void ExpandTimeRanges_MultipleChunks_VaryingLengths()
    {
        // Arrange
        var input = ToChunks(
            [Ms(100, 200)],
            [Ms(500, 600), Ms(800, 900), Ms(1200, 1300)],
            [Ms(2000, 2100)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        Assert.Equal(3, actual.Length);
        Assert.Single(actual[0]);
        Assert.Single(actual[1]);
        Assert.Single(actual[2]);

        // Check all (From < To)
        foreach (var chunk in actual)
        {
            foreach (var range in chunk)
            {
                Assert.True(range.From.TotalMilliseconds < range.To.TotalMilliseconds);
            }
        }
    }


    [Fact]
    public void ExpandTimeRanges_FirstRangeAtZero_NoNegativeExpansion()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 500), Ms(1500, 2000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, thresholdMillesecods: 500);

        // Assert
        Assert.Equal(0, actual[0][0].From.TotalMilliseconds);
        Assert.True(actual[0][0].To.TotalMilliseconds > 500);
    }


    [Fact]
    public void ExpandTimeRanges_DefaultThreshold_Uses100ms()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 1000), Ms(1500, 2500)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, 100);


        var expected = ToChunks(
            [Ms(0, 1100), Ms(1400, 2600)]
        );

        // Assert
        AssertChunksEqual(expected, actual);
    }




    [Fact]
    public void ExpandTimeRanges_PreservesChunkOrder()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 1100)],
            [Ms(2000, 2100)],
            [Ms(3000, 3100)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(
            input,
            thresholdMillesecods: 0,
            gapMilliseconds: 500);

        var expected = ToChunks(
            [Ms(1000, 1100)],
            [Ms(2000, 2100)],
            [Ms(3000, 3100)]
        );

        AssertChunksEqual(expected, actual);
    }


    [Fact]
    public void ExpandTimeRanges_NegativeDeltas_NotApplied()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 2000), Ms(2100, 3000)]  // gap=100 < 500
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(
            input,
            thresholdMillesecods: 0,
            gapMilliseconds: 500);


        var expected = ToChunks(
            [Ms(1000, 2000), Ms(2100, 3000)]
        );

        AssertChunksEqual(expected, actual);
    }


    [Fact]
    public void ExpandTimeRanges_VeryLargeGap()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 1000), Ms(2000, 3000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(
            input,
            thresholdMillesecods: 1500,
            gapMilliseconds: 1500);

        var expected = ToChunks([Ms(0, 4500)]);

        AssertChunksEqual(expected, actual);
    }
}
