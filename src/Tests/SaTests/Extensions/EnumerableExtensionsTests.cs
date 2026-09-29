using Sa.Extensions;

namespace SaTests.Extensions;

/// <summary>
/// Регрессия на <c>JoinByString</c>: подпись обещает <c>string</c>, а <c>default!</c> возвращал
/// <c>null</c>, то есть врал всем вызывающим.
/// </summary>
public class EnumerableExtensionsTests
{
    [Fact]
    public void JoinByString_NullSource_ReturnsEmptyStringNotNull()
    {
        // Arrange
        IEnumerable<string> source = null!;

        // Act
        var result = source.JoinByString();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void JoinByString_MapOverload_NullSource_ReturnsEmptyStringNotNull()
    {
        IEnumerable<string> source = null!;

        var result = source.JoinByString(s => s.ToUpperInvariant());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void JoinByString_MapWithIndexOverload_NullSource_ReturnsEmptyStringNotNull()
    {
        IEnumerable<string> source = null!;

        var result = source.JoinByString((s, _) => s.ToUpperInvariant());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(null, "abc")]        // null — разделитель не вставляется
    [InlineData(",", "a,b,c")]
    [InlineData("|", "a|b|c")]
    [InlineData(" - ", "a - b - c")]
    public void JoinByString_JoinSeparator_IsUsed(string? joinWith, string expected)
    {
        string[] source = ["a", "b", "c"];

        Assert.Equal(expected, source.JoinByString(joinWith));
        Assert.Equal(expected, source.JoinByString(s => s, joinWith));
    }

    [Fact]
    public void JoinByString_MapOverload_MapsEveryElement()
    {
        // map обязан возвращать T, поэтому преобразование в строку возможно только
        // для строкового источника, а числовой проверяется индексированной формой.
        string[] words = ["a", "b", "c"];
        int[] numbers = [1, 2, 3];

        Assert.Equal("A,B,C", words.JoinByString(s => s.ToUpperInvariant(), ","));
        // 1*1+0, 2*2+1, 3*3+2
        Assert.Equal("1,5,11", numbers.JoinByString((i, idx) => i * i + idx, ","));
    }

    [Fact]
    public void JoinByString_MapOverload_HandlesCollectionAndSequenceEqually()
    {
        // ICollection<T> идёт по ветке с копированием в массив, обычная последовательность —
        // через LINQ; результат должен совпадать.
        int[] source = [1, 2, 3, 4];
        IEnumerable<int> lazy = source.Where(_ => true);

        Assert.Equal(source.JoinByString(i => i * 2, ","), lazy.JoinByString(i => i * 2, ","));
        Assert.Equal(source.JoinByString(), lazy.JoinByString());
    }
}
