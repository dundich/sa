using Sa.Classes;

namespace SaTests.Classes;

/// <summary>
/// Регрессия на <c>IsEmpty</c>: сравнение с sentinel'ом <c>Empty = new(default, default)</c>
/// по record-равенству объявляло пустой любую законно построенную точку для value-типа T.
/// </summary>
public class SectionTests
{
    [Theory]
    [InlineData(0, 0, false)]   // точка, а не пустая секция
    [InlineData(0, 1, false)]
    [InlineData(5, 10, false)]
    [InlineData(0, 5, false)]
    [InlineData(1, 0, true)]    // развёрнутая — ни одного значения не содержит
    public void IsEmpty_PlainSection_OrderedSectionIsNotEmpty(int start, int end, bool expected)
    {
        Assert.Equal(expected, new Section<int>(start, end).IsEmpty());
    }

    [Fact]
    public void IsEmpty_PlainSection_InvertedIsEmpty()
    {
        Assert.True(new Section<int>(10, 0).IsEmpty());
    }

    [Fact]
    public void IsEmpty_PlainSection_DefaultValuesAreNotEmpty()
    {
        // Раньше ровно этот случай возвращал true: new Section<int>(0, 0) == Section<int>.Empty.
        Assert.False(new Section<int>(0, 0).IsEmpty());
        Assert.False(new Section<int>(default, default).IsEmpty());
    }

    [Fact]
    public void IsEmpty_TimeSpanSection_ZeroWidthSectionIsNotEmpty()
    {
        var section = new Section<TimeSpan>(TimeSpan.Zero, TimeSpan.Zero);
        Assert.False(section.IsEmpty());
    }

    [Fact]
    public void IsEmpty_LimSection_ExclusiveEqualBoundsAreEmpty()
    {
        // (0, 0) не содержит ни одного значения, [0, 0] содержит точку 0.
        Assert.True(new LimSection<int>(0, 0, HasEnd: false).IsEmpty());
        Assert.False(new LimSection<int>(0, 0, HasEnd: true).IsEmpty());
        Assert.False(new LimSection<int>(0, 1, HasEnd: false).IsEmpty());
        Assert.True(new LimSection<int>(5, 1).IsEmpty());
    }

    [Fact]
    public void IsEmpty_HalfSection_UnboundedEndIsNotEmpty()
    {
        // End == null означает бесконечность, а не пустоту.
        Assert.False(new HalfSection<string>("a", null).IsEmpty());
        Assert.False(new HalfSection<string>("a", "z").IsEmpty());
        Assert.True(new HalfSection<string>("z", "a").IsEmpty());
    }

    [Fact]
    public void IsEmpty_AgreesWithIsPointAndInRange()
    {
        foreach (var (start, end) in new[] { (0, 0), (0, 5), (5, 0), (-3, 7) })
        {
            var section = new Section<int>(start, end);
            var containsNoValue = !Enumerable.Range(start, Math.Max(0, end - start + 1)).Any(section.InRange);

            Assert.Equal(containsNoValue, section.IsEmpty());
        }
    }

    [Fact]
    public void IsEmpty_RecordEquality_NoLongerCollidesWithSentinel()
    {
        // То, из-за чего исходный IsEmpty врал: (0, 0) и (default, default) — один и тот же объект.
        Assert.Equal(new Section<int>(0, 0), new Section<int>(default, default));
    }

    #region MergeIntervals

    [Fact]
    public void MergeIntervals_EmptyInput_ReturnsEmptyResult()
    {
        // Раньше: currentInterval = sortedList[0] на пустом списке -> ArgumentOutOfRangeException.
        // Объединять нечего — пустой вход даёт пустой выход.
        var result = RangeExtensions.MergeIntervals<int>([]);

        Assert.Empty(result);
    }

    [Fact]
    public void MergeIntervals_SingleInterval_ReturnsItUnchanged()
    {
        var result = RangeExtensions.MergeIntervals([new Section<int>(10, 20)]);

        Assert.Equal([new Section<int>(10, 20)], result);
    }

    [Fact]
    public void MergeIntervals_UnsortedOverlapping_AreMerged()
    {
        var result = RangeExtensions.MergeIntervals(
        [
            new Section<int>(30, 40),
            new Section<int>(10, 20),
            new Section<int>(15, 35),
        ]);

        Assert.Equal([new Section<int>(10, 40)], result);
    }

    [Fact]
    public void MergeIntervals_TouchingSections_AreMerged()
    {
        var result = RangeExtensions.MergeIntervals([new Section<int>(10, 20), new Section<int>(20, 30)]);

        Assert.Equal([new Section<int>(10, 30)], result);
    }

    [Fact]
    public void MergeIntervals_DisjointSections_AreKeptSeparate()
    {
        var result = RangeExtensions.MergeIntervals([new Section<int>(10, 20), new Section<int>(30, 40)]);

        Assert.Equal([new Section<int>(10, 20), new Section<int>(30, 40)], result);
    }

    [Fact]
    public void MergeIntervals_FirstSectionNotDuplicated()
    {
        // Раньше цикл начинался с первого элемента, то есть currentInterval сравнивался сам с
        // собой. У развёрнутого первого отрезка сравнение не выполнялось, и в результат
        // попадал он дважды.
        var result = RangeExtensions.MergeIntervals([new Section<int>(20, 10), new Section<int>(30, 40)]);

        Assert.Equal([new Section<int>(20, 10), new Section<int>(30, 40)], result);
    }

    #endregion

    #region FindEmptyIntervals

    [Fact]
    public void FindEmptyIntervals_EntireRangeFree_ReturnsWholeRange()
    {
        var result = new Section<int>(10, 100).FindEmptyIntervals([]);

        Assert.Equal([new Section<int>(10, 100)], result);
    }

    [Fact]
    public void FindEmptyIntervals_BusySectionEntirelyBeforeRange_ReturnsWholeRange()
    {
        // Раньше currentStart получал конец каждого занятого отрезка безусловно, поэтому граница
        // уезжала назад и ответ начинался левее запроса: [10,100] минус [0,5] давало [5,100].
        var result = new Section<int>(10, 100).FindEmptyIntervals([new Section<int>(0, 5)]);

        Assert.Equal([new Section<int>(10, 100)], result);
    }

    [Fact]
    public void FindEmptyIntervals_BusySectionEntirelyAfterRange_ReturnsWholeRange()
    {
        // Раньше: currentStart = 300, 100 > 300 ложно, и возвращался пустой список —
        // хотя весь диапазон был свободен.
        var result = new Section<int>(10, 100).FindEmptyIntervals([new Section<int>(200, 300)]);

        Assert.Equal([new Section<int>(10, 100)], result);
    }

    [Fact]
    public void FindEmptyIntervals_BusySectionInTheMiddle_SplitsRange()
    {
        var result = new Section<int>(10, 100).FindEmptyIntervals([new Section<int>(20, 30)]);

        Assert.Equal([new Section<int>(10, 20), new Section<int>(30, 100)], result);
    }

    [Fact]
    public void FindEmptyIntervals_BusySectionCoversRange_ReturnsEmpty()
    {
        Assert.Empty(new Section<int>(10, 100).FindEmptyIntervals([new Section<int>(10, 100)]));
        Assert.Empty(new Section<int>(10, 100).FindEmptyIntervals([new Section<int>(0, 200)]));
    }

    [Fact]
    public void FindEmptyIntervals_UnsortedOverlappingBusy_SectionsAreMerged()
    {
        var result = new Section<int>(10, 100).FindEmptyIntervals(
        [
            new Section<int>(60, 70),
            new Section<int>(0, 20),
            new Section<int>(15, 30),
            new Section<int>(200, 300),
        ]);

        Assert.Equal([new Section<int>(30, 60), new Section<int>(70, 100)], result);
    }

    [Fact]
    public void FindEmptyIntervals_AdjacentBusySections_DoNotProduceZeroWidthGaps()
    {
        var result = new Section<int>(10, 100).FindEmptyIntervals(
        [
            new Section<int>(20, 30),
            new Section<int>(30, 40),
        ]);

        Assert.Equal([new Section<int>(10, 20), new Section<int>(40, 100)], result);
        Assert.All(result, s => Assert.False(s.IsPoint()));
    }

    [Fact]
    public void FindEmptyIntervals_DegenerateRange_ReturnsEmpty()
    {
        Assert.Empty(new Section<int>(10, 10).FindEmptyIntervals([]));
        Assert.Empty(new Section<int>(10, 10).FindEmptyIntervals([new Section<int>(10, 10)]));
    }

    [Fact]
    public void FindEmptyIntervals_InvertedRange_ReturnsEmpty()
    {
        Assert.Empty(new Section<int>(100, 10).FindEmptyIntervals([]));
        Assert.Empty(new Section<int>(100, 10).FindEmptyIntervals([new Section<int>(50, 60)]));
    }

    #endregion

    #region FindIntersections

    [Fact]
    public void FindIntersections_Overlapping_ReturnsOverlap()
    {
        var result = new Section<int>(10, 30).FindIntersections(new Section<int>(20, 40));

        Assert.Equal(new Section<int>(20, 30), result);
        Assert.False(result!.IsPoint());
    }

    [Fact]
    public void FindIntersections_Touching_ReturnsPoint()
    {
        // Section<T> — закрытый отрезок (InRange включает обе границы), поэтому касание
        // концом есть пересечение, и оно вырождается в точку. Это не дефект, а контракт:
        // пустой результат — только null, а касание от перекрытия отличает IsPoint.
        var result = new Section<int>(10, 20).FindIntersections(new Section<int>(20, 30));

        Assert.Equal(new Section<int>(20, 20), result);
        Assert.True(result!.IsPoint());
        Assert.False(result!.IsEmpty());
    }

    [Theory]
    [InlineData(30, 40)]   // справа
    [InlineData(0, 5)]     // слева
    public void FindIntersections_Disjoint_ReturnsNull(int otherStart, int otherEnd)
    {
        var result = new Section<int>(10, 20).FindIntersections(new Section<int>(otherStart, otherEnd));

        Assert.Null(result);
    }

    [Fact]
    public void FindIntersections_Contained_ReturnsTheInnerSection()
    {
        var result = new Section<int>(0, 100).FindIntersections(new Section<int>(20, 30));

        Assert.Equal(new Section<int>(20, 30), result);
    }

    [Fact]
    public void FindIntersections_IsCommutative()
    {
        var a = new Section<int>(10, 30);
        var b = new Section<int>(20, 40);

        Assert.Equal(a.FindIntersections(b), b.FindIntersections(a));
    }

    #endregion
}
