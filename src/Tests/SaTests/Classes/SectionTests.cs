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
}
