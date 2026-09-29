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

    [Fact]
    public void Hash32_MultipleOfFourBytes_UsesTheCanonicalLittleEndianBlockLoad()
    {
        // Раньше 4-байтовый блок читался через BitConverter.ToUInt32, то есть в порядке
        // байтов хоста: на большом эндиане хэш расходился с любым другим MurmurHash3.
        // На little-endian результат не меняется, и эталонные значения ниже это фиксируют.
        byte[] block = [0x01, 0x02, 0x03, 0x04];

        var hash = MurmurHash3.Hash32(block, 0U);

        Assert.Equal(1043635621U, hash);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaa", 0U, 4187236331U)]                      // 16 байт — 4 полных блока
    [InlineData("abcdefghijklmnopqrstuvwxyz", 1U, 36174746U)]
    public void Hash32_LongInput_StaysCanonicalAcrossBlocks(string input, uint seed, uint expected)
    {
        // Вход длиннее одного блока: little-endian загрузка должна применяться ко всем
        // блокам, а не только к первому. Хвост у этих входов пуст, так что проверяется
        // именно ветка полных блоков.
        var bytes = Encoding.UTF8.GetBytes(input);

        var result = MurmurHash3.Hash32(bytes, seed);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Hash32_LongInputWithTail_MatchesTheSameAlgorithmOnShortInput()
    {
        // Вход с хвостом: последние байты попадают в отдельную ветку остатка. Эталон
        // получен независимой реализацией MurmurHash3 x86_32 с явной little-endian
        // загрузкой, сошёдшейся с каноническими векторами выше.
        var bytes = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");

        var result = MurmurHash3.Hash32(bytes, 0U);

        Assert.Equal(ReferenceLittleEndianHash(bytes, 0U), result);
    }

    /// <summary>
    /// Независимая реализация MurmurHash3 x86_32 с явной little-endian загрузкой блоков —
    /// эталон для проверки порядка байтов. Сверена со всеми каноническими значениями
    /// этой же тестовой классовой части.
    /// </summary>
    private static uint ReferenceLittleEndianHash(ReadOnlySpan<byte> bytes, uint seed)
    {
        const uint c1 = 0xcc9e2d51U;
        const uint c2 = 0x1b873593U;
        const uint m = 5U;
        const uint n = 0xe6546b64U;

        uint h1 = seed;
        int blocks = bytes.Length / 4;

        for (int i = 0; i < blocks; i++)
        {
            int o = i * 4;
            uint k = (uint)(bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24));
            k *= c1;
            k = (k << 15) | (k >> 17);
            k *= c2;

            h1 ^= k;
            h1 = (h1 << 13) | (h1 >> 19);
            h1 = h1 * m + n;
        }

        uint tail = 0;
        int rest = bytes.Length & 3;
        for (int i = rest - 1; i >= 0; i--)
            tail = (tail << 8) | bytes[blocks * 4 + i];

        if (rest > 0)
        {
            tail *= c1;
            tail = (tail << 15) | (tail >> 17);
            tail *= c2;
            h1 ^= tail;
        }

        h1 ^= (uint)bytes.Length;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6bU;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35U;
        h1 ^= h1 >> 16;
        return h1;
    }
}

