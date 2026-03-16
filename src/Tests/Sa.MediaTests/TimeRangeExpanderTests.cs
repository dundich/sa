using Sa.Media;

namespace Sa.MediaTests;

public class TimeRangeExpanderTests
{
    #region Helper Methods

    private static TimeRange Ms(long from, long to) =>
        TimeRange.RangeFromMilliseconds(from, to);

    private static TimeRange[][] ToChunks(params TimeRange[][] chunks) => chunks;

    private static void AssertTimeRangesEqual(TimeRange expected, TimeRange actual)
    {
        Assert.NotNull(actual);
        Assert.NotNull(expected);

        Assert.NotNull(actual.To);
        Assert.NotNull(expected.To);

        Assert.Equal(
            expected.From.TotalMilliseconds,
            actual.From.TotalMilliseconds,
            precision: 0);

        Assert.Equal(
            expected.To.Value.TotalMilliseconds,
            actual.To.Value.TotalMilliseconds,
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

    #region Тест 1: Базовое расширение с положительными дельтами

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
                Ms(650, 2100),    // 800-150, 1900+100
                Ms(5000, 5800),   // без изменений (дельты ≤ 0)
                Ms(7200, 9750),   // без изменений (дельты ≤ 0)
               // Ms(9000, 9750)    // 9700+50
            ],
            [
                Ms(2600, 4600),   // 2800-200
                Ms(6300, 6899),   // без изменений
                Ms(10250, 10700)  // 10300-50
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }

    #endregion

    #region Тест 2: Слияние близких диапазонов внутри чанка

    [Fact]
    public void ExpandTimeRanges_MergeCloseRanges_WithinChunk()
    {
        // Arrange
        var input = ToChunks(
            [
                Ms(1000, 2000),
                Ms(2300, 3000),   // gap=300 < 500 → сольётся с предыдущим
                Ms(5000, 6000)
            ],
            [
                Ms(3500, 4000)
            ]
        );

        // После слияния: [1000-3000], [5000-6000]
        var expected = ToChunks(
            [
                Ms(875, 3125),    // объединённый [1000-3000] + расширение
                Ms(4875, 6125)    // [5000-6000] + расширение
            ],
            [
                Ms(3375, 4125)    // [3500-4000] + расширение
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }

    #endregion

    #region Тест 3: Пустые массивы

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

    #endregion

    #region Тест 4: Единичный диапазон

    [Fact]
    public void ExpandTimeRanges_SingleRange_ExpandsBothSides()
    {
        // Arrange
        var input = ToChunks(
            [Ms(5000, 6000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.Single(actual);
        Assert.Single(actual[0]);
        Assert.NotNull(actual[0][0].To);

        // Проверяем, что диапазон расширился
        Assert.True(actual[0][0].From.TotalMilliseconds <= 5000);
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds >= 6000);
    }

    #endregion

    #region Тест 5: Диапазоны уже с достаточным зазором

    [Fact]
    public void ExpandTimeRanges_SufficientGap_ExpandsFully()
    {
        // Arrange
        var input = ToChunks(
            [
                Ms(0, 1000),
                Ms(2000, 3000)    // gap=1000 > 500 → есть место для расширения
            ]
        );

        var expected = ToChunks(
            [
                Ms(0, 1250),      // 1000 + (2000-1000-500)/2 = 1000+250
                Ms(1750, 3000)    // 2000 - (2000-1000-500)/2 = 2000-250
            ]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }

    #endregion

    #region Тест 6: Диапазоны слишком близко (без слияния, т.к. разные чанки)

    [Fact]
    public void ExpandTimeRanges_CloseRanges_DifferentChunks_NoMerge()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 2000)],
            [Ms(2100, 3000)]    // gap=100 < 500, но разные чанки → не сливаются
        );

        // Дельты отрицательные → без расширения
        var expected = ToChunks(
            [Ms(750, 2000)],
            [Ms(2100, 3000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        AssertChunksEqual(expected, actual);
    }

    #endregion

    #region Тест 7: Несколько чанков с разным количеством диапазонов

    [Fact]
    public void ExpandTimeRanges_MultipleChunks_VaryingLengths()
    {
        // Arrange
        var input = ToChunks(
            [Ms(100, 200)],                              // чанк 0: 1 диапазон
            [Ms(500, 600), Ms(800, 900), Ms(1200, 1300)], // чанк 1: 3 диапазона
            [Ms(2000, 2100)]                             // чанк 2: 1 диапазон
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.Equal(3, actual.Length);
        Assert.Single(actual[0]);
        Assert.Equal(3, actual[1].Length);
        Assert.Single(actual[2]);

        // Проверка, что все диапазоны валидны (From < To)
        foreach (var chunk in actual)
        {
            foreach (var range in chunk)
            {
                Assert.NotNull(range.To);
                Assert.True(range.From.TotalMilliseconds < range.To.Value.TotalMilliseconds,
                    "диапазон должен быть валидным (From < To)");
            }
        }
    }

    #endregion

    #region Тест 8: Граница с нулём (первый диапазон начинается с 0)

    [Fact]
    public void ExpandTimeRanges_FirstRangeAtZero_NoNegativeExpansion()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 500), Ms(1500, 2000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.Equal(0, actual[0][0].From.TotalMilliseconds);
        Assert.NotNull(actual[0][0].To);
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds > 500);
    }

    #endregion

    #region Тест 9: Большой зазор между диапазонами

    [Fact]
    public void ExpandTimeRanges_LargeGap_ExpandsSignificantly()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 100), Ms(5000, 5100)]  // gap=4900 >> 500
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.NotNull(actual[0][0].To);
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds > 100);
        Assert.True(actual[0][1].From.TotalMilliseconds < 5000);

        // Проверка, что зазор остался >= 500
        var gap = actual[0][1].From.TotalMilliseconds - actual[0][0].To!.Value.TotalMilliseconds;
        Assert.True(gap >= 500, $"минимальный зазор должен сохраниться. Фактический зазор: {gap}");
    }

    #endregion

    #region Тест 10: Default gap (100ms)

    [Fact]
    public void ExpandTimeRanges_DefaultGap_Uses100ms()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 1000), Ms(1500, 2500)]  // gap=500
        );

        // Act (gap по умолчанию = 100)
        var actual = TimeRangeExpander.ExpandTimeRanges(input);

        // Assert
        Assert.NotNull(actual[0][0].To);
        // При gap=100: delta = (1500-1000-100)/2 = 200
        AssertApproxEqual(1200, (long)actual[0][0].To!.Value.TotalMilliseconds, tolerance: 1);
        AssertApproxEqual(1300, (long)actual[0][1].From.TotalMilliseconds, tolerance: 1);
    }

    #endregion

    #region Тест 11: Null проверка To в TimeRange

    [Fact]
    public void ExpandTimeRanges_ToNotNull_AllRangesHaveTo()
    {
        // Arrange
        var input = ToChunks(
            [Ms(100, 200), Ms(300, 400)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 50);

        // Assert
        foreach (var chunk in actual)
        {
            foreach (var range in chunk)
            {
                Assert.NotNull(range.To);
            }
        }
    }

    #endregion

    #region Тест 12: Сохранение порядка чанков

    [Fact]
    public void ExpandTimeRanges_PreservesChunkOrder()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 1100)],  // чанк 0
            [Ms(2000, 2100)],  // чанк 1
            [Ms(3000, 3100)]   // чанк 2
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert
        Assert.Equal(3, actual.Length);
        Assert.Single(actual[0]);
        Assert.Single(actual[1]);
        Assert.Single(actual[2]);

        // Проверяем, что диапазоны остались в своих чанках
        Assert.Equal(1000, actual[0][0].From.TotalMilliseconds, precision: 0);
        Assert.Equal(2000, actual[1][0].From.TotalMilliseconds, precision: 0);
        Assert.Equal(3000, actual[2][0].From.TotalMilliseconds, precision: 0);
    }

    #endregion

    #region Тест 13: Отрицательные дельты не применяются

    [Fact]
    public void ExpandTimeRanges_NegativeDeltas_NotApplied()
    {
        // Arrange
        var input = ToChunks(
            [Ms(1000, 2000), Ms(2100, 3000)]  // gap=100 < 500
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 500);

        // Assert - диапазоны не должны расширяться при отрицательных дельтах
        Assert.NotNull(actual[0][0].To);
        Assert.NotNull(actual[0][1].To);

        // Границы остаются на месте или сдвигаются минимально
        Assert.True(actual[0][0].From.TotalMilliseconds <= 1000);
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds <= 2000);
        Assert.True(actual[0][1].From.TotalMilliseconds >= 2100);
        Assert.True(actual[0][1].To!.Value.TotalMilliseconds >= 3000);
    }

    #endregion

    #region Тест 14: Очень маленький gap

    [Fact]
    public void ExpandTimeRanges_VerySmallGap_MoreExpansion()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 1000), Ms(2000, 3000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 10);

        // Assert
        Assert.NotNull(actual[0][0].To);
        // При gap=10 больше пространства для расширения
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds > 1000);
        Assert.True(actual[0][1].From.TotalMilliseconds < 2000);
    }

    #endregion

    #region Тест 15: Очень большой gap

    [Fact]
    public void ExpandTimeRanges_VeryLargeGap_LimitedExpansion()
    {
        // Arrange
        var input = ToChunks(
            [Ms(0, 1000), Ms(2000, 3000)]
        );

        // Act
        var actual = TimeRangeExpander.ExpandTimeRanges(input, gapMilliseconds: 1500);

        // Assert
        Assert.NotNull(actual[0][0].To);
        // При gap=1500 меньше пространства для расширения
        Assert.True(actual[0][0].To!.Value.TotalMilliseconds <= 1250);
        Assert.True(actual[0][1].From.TotalMilliseconds >= 1750);
    }

    #endregion
}
