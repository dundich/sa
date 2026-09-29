using Sa.Extensions;

namespace SaTests.Extensions;

/// <summary>
/// Регрессия на разбиение на чанки: неположительный <c>chunkSize</c> не проверялся, и поведение
/// зависело от того, перечисляют ли результат.
/// </summary>
public class SpanExtensionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GetChunks_NonPositiveChunkSize_ThrowsArgumentOutOfRangeException(int chunkSize)
    {
        // Arrange
        int[] source = [1, 2, 3];
        Memory<int> arr = source;

        // Act — вызов без перечисления: проверка обязана быть в методе, а не в итераторе,
        // иначе до неё дело не доходит и перечисление зависает (i += 0 не сдвигает счётчик).
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => arr.GetChunks(chunkSize));

        // Assert
        Assert.Equal("chunkSize", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void GetChunksArray_NonPositiveChunkSize_ThrowsArgumentOutOfRangeException(int chunkSize)
    {
        // Arrange
        int[] source = [1, 2, 3];
        Memory<int> arr = source;

        // Act
        // Раньше: (arr.Length + 0 - 1) / 0 -> DivideByZeroException, отрицательный размер ->
        // OverflowException внутри new Memory<T>[count].
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => arr.GetChunksArray(chunkSize));

        // Assert
        Assert.Equal("chunkSize", ex.ParamName);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(5, 1)]
    [InlineData(7, 1)]
    public void GetChunks_PositiveChunkSize_CoversEveryElementExactly(int chunkSize, int expectedChunks)
    {
        // Arrange — 5 элементов
        int[] source = [1, 2, 3, 4, 5];
        Memory<int> arr = source;

        // Act
        var chunks = arr.GetChunks(chunkSize).ToArray();

        // Assert
        Assert.Equal(expectedChunks, chunks.Length);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, chunkSize));
        Assert.Equal(source, chunks.SelectMany(c => c.ToArray()));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(5, 1)]
    [InlineData(7, 1)]
    public void GetChunksArray_PositiveChunkSize_MatchesGetChunks(int chunkSize, int expectedChunks)
    {
        // Arrange — 5 элементов
        int[] source = [1, 2, 3, 4, 5];
        Memory<int> arr = source;

        // Act
        var chunks = arr.GetChunksArray(chunkSize);
        var lazy = arr.GetChunks(chunkSize).ToArray();

        // Assert
        Assert.Equal(expectedChunks, chunks.Length);
        Assert.Equal(lazy.Length, chunks.Length);
        for (int i = 0; i < chunks.Length; i++)
            Assert.Equal(lazy[i].ToArray(), chunks[i].ToArray());
    }

    [Fact]
    public void GetChunks_EmptyArray_ReturnsNoChunks()
    {
        // Arrange
        Memory<int> arr = Memory<int>.Empty;

        // Assert
        Assert.Empty(arr.GetChunks(3));
        Assert.Empty(arr.GetChunksArray(3));
    }
}
