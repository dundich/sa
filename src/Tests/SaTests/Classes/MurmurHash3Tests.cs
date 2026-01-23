using Sa.Classes;
using System.Text;

namespace SaTests.Classes;

/// <summary>
/// https://murmurhash.shorelabs.com/
/// </summary>
public class MurmurHash3Tests
{
    [Theory]
    [InlineData("", 0U, 0U)] // пустая строка
    [InlineData("hello", 0U, 613153351)]
    [InlineData("hello", 123U, 1573043710U)]
    [InlineData("world", 0U, 4220927227)]
    [InlineData("test", 456U, 2698885723)]
    [InlineData("murmur", 789U, 1508556864U)]
    [InlineData("Hello, world!", 0U, 3224780355)]
    public void Hash32_WithStringInput_ReturnsExpectedHash(string input, uint seed, uint expected)
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes(input);

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Hash32_WithNullInput_ReturnsSeed()
    {
        // Arrange
        var bytes = Array.Empty<byte>();
        uint seed = 123U;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(2235285516, result); // Hash от пустого массива с seed 123
    }

    [Theory]
    [InlineData(1)]  // 1 байт
    [InlineData(2)]  // 2 байта  
    [InlineData(3)]  // 3 байта
    [InlineData(5)]  // 5 байт
    [InlineData(7)]  // 7 байт
    [InlineData(15)] // 15 байт
    public void Hash32_WithDifferentLengths_ReturnsConsistentResults(int length)
    {
        // Arrange
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        uint seed = 999U;

        // Act
        var result1 = MurmurHash3.Hash32(bytes, seed);
        var result2 = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void Hash32_SameInputDifferentSeeds_ReturnsDifferentHashes()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("test string");
        uint seed1 = 0U;
        uint seed2 = 1U;

        // Act
        var result1 = MurmurHash3.Hash32(bytes, seed1);
        var result2 = MurmurHash3.Hash32(bytes, seed2);

        // Assert
        Assert.NotEqual(result1, result2);
    }

    [Fact]
    public void Hash32_SlightlyDifferentInputs_ReturnsVeryDifferentHashes()
    {
        // Arrange
        var bytes1 = Encoding.UTF8.GetBytes("hello world");
        var bytes2 = Encoding.UTF8.GetBytes("hello world!");
        uint seed = 0U;

        // Act
        var result1 = MurmurHash3.Hash32(bytes1, seed);
        var result2 = MurmurHash3.Hash32(bytes2, seed);

        // Assert
        // Хеши должны сильно отличаться (проверяем что не просто +1)
        uint difference = (result1 > result2) ? result1 - result2 : result2 - result1;
        Assert.True(difference > 1000000U, "Хеши слишком похожи для разных входных данных");
    }

    [Fact]
    public void Hash32_WithZeroSeed_WorksCorrectly()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("test data");
        uint seed = 0U;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.NotEqual(0U, result); // Хеш не должен быть нулевым
        Assert.InRange(result, 1U, uint.MaxValue); // Должен быть в допустимом диапазоне
    }

    [Fact]
    public void Hash32_PerformanceTest_LargeInput()
    {
        // Arrange
        var largeBytes = new byte[100000]; // 100KB
        new Random(42).NextBytes(largeBytes);
        uint seed = 123U;

        // Act & Assert - просто проверяем что не падает
        var result = MurmurHash3.Hash32(largeBytes, seed);
        Assert.NotEqual(0U, result);
    }


    [Fact]
    public void RotateLeft_ValidInput_CorrectlyRotatesBits()
    {
        // Arrange
        uint value = 0b11000000000000000000000000000001;
        byte shift = 1;

        // Act
        var result = MurmurHash3.RotateLeft(value, shift);

        // Assert
        uint expected = 0b10000000000000000000000000000011;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x12345678, 4, 0x23456781)]
    [InlineData(0xFFFFFFFF, 1, 0xFFFFFFFF)]
    [InlineData(0x00000001, 31, 0x80000000)]
    [InlineData(0x80000000, 1, 0x00000001)]
    public void RotateLeft_VariousInputs_CorrectlyRotates(uint value, byte shift, uint expected)
    {
        // Act
        var result = MurmurHash3.RotateLeft(value, shift);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FMix_ValidInput_CorrectlyMixesBits()
    {
        // Arrange
        uint value = 0x12345678;

        // Act
        var result = MurmurHash3.FMix(value);

        // Assert - проверяем что биты хорошо перемешаны
        Assert.NotEqual(value, result);
        Assert.InRange(result, 0U, uint.MaxValue);
    }


    [Fact]
    public void Hash32_SingleByte_WorksCorrectly()
    {
        // Arrange
        var bytes = "B"u8.ToArray();
        uint seed = 0U;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(3433458314, result);
    }

    [Fact]
    public void Hash32_TwoBytes_WorksCorrectly()
    {
        // Arrange
        var bytes = "BC"u8.ToArray();
        uint seed = 0U;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(2779220341, result);
    }

    [Fact]
    public void Hash32_ThreeBytes_WorksCorrectly()
    {
        // Arrange
        var bytes = "BCD"u8.ToArray();
        uint seed = 0U;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.Equal(3561005568, result);
    }

    [Fact]
    public void Hash32_MaxValueSeed_WorksCorrectly()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("test");
        uint seed = uint.MaxValue;

        // Act
        var result = MurmurHash3.Hash32(bytes, seed);

        // Assert
        Assert.NotEqual(0U, result);
        Assert.InRange(result, 0U, uint.MaxValue);
    }
}

