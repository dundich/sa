using Sa.Classes;
using Sa.Extensions;
using System.Text;

namespace SaTests;

using Xunit;

public class StringExtensionsTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("  hello  world  ", " hello world ")]
    [InlineData("hello\tworld", "hello world")]
    [InlineData("\t\nhello\r\nworld\t\n", " hello world ")]
    [InlineData("  multiple   spaces  ", " multiple spaces ")]
    [InlineData("    ", " ")]
    [InlineData("\t\n", " ")]
    public void NormalizeWhiteSpace_NoTrimmed_ReturnsExpectedResult(string? input, string? expected)
    {
        // Act
        var result = input.NormalizeWhiteSpace(isTrimmed: false);

        // Assert
        Assert.Equal(expected, result);
    }


    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  hello  world  ", "hello world")]
    [InlineData("hello\tworld", "hello world")]
    [InlineData("hello\nworld", "hello world")]
    [InlineData("hello\r\nworld", "hello world")]
    [InlineData("hello\t \n \r\n world", "hello world")]
    [InlineData("  multiple   spaces\tbetween\nwords  ", "multiple spaces between words")]
    [InlineData("line1\nline2\nline3", "line1 line2 line3")]
    [InlineData("  \t\n\r  test  \t\n\r  ", "test")]
    public void NormalizeWhiteSpace_WithTrimmed_ReturnsExpectedResult(string? input, string? expected)
    {
        // Act
        var result = input.NormalizeWhiteSpace(isTrimmed: true);

        // Assert
        Assert.Equal(expected, result);
    }



    [Fact]
    public void NormalizeWhiteSpace_EmptyString_ReturnsEmpty()
    {
        // Arrange
        var input = "";

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_NullInput_ReturnsEmptyString()
    {
        // Arrange
        string? input = null;

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeWhiteSpace_OnlyWhitespaceWithTrimmed_ReturnsEmpty()
    {
        // Arrange
        var input = "   \t\n\r   ";

        // Act
        var result = input.NormalizeWhiteSpace(isTrimmed: true);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_OnlyWhitespaceWithoutTrimmed_ReturnsSingleSpace()
    {
        // Arrange
        var input = "   \t\n\r   ";

        // Act
        var result = input.NormalizeWhiteSpace(isTrimmed: false);

        // Assert
        Assert.Equal(" ", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_MixedWhitespaceCharacters_ReplacesWithSingleSpace()
    {
        // Arrange
        var input = "a\tb\nc\rd e";

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal("a b c d e", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_AlreadyNormalized_ReturnsSame()
    {
        // Arrange
        var input = "already normalized text";

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal(input, result);
    }

    [Fact]
    public void NormalizeWhiteSpace_MultipleSpacesBetweenWords_ReducesToOneSpace()
    {
        // Arrange
        var input = "word1   word2    word3";

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal("word1 word2 word3", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_ComplexMultilineText_HandlesCorrectly()
    {
        // Arrange
        var input = @"
            Line 1 with   spaces
            Line 2 with	tabs
            Line 3 with

            empty lines
        ";

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal("Line 1 with spaces Line 2 with tabs Line 3 with empty lines", result);
    }

    [Fact]
    public void NormalizeWhiteSpace_UnicodeWhitespace_HandlesCorrectly()
    {
        // Arrange
        var input = "hello\u00A0\u2000\u2001world"; // Different unicode spaces

        // Act
        var result = input.NormalizeWhiteSpace();

        // Assert
        Assert.Equal("hello world", result);
    }

    #region NormalizeWhiteSpaceSpan

    [Theory]
    [InlineData("hello  world", "hello world")]
    [InlineData("  hello world  ", "hello world")]
    [InlineData("hello\tworld", "hello world")]
    [InlineData("a b c d", "a b c d")]
    public void NormalizeWhiteSpaceSpan_FitsBuffer_WritesNormalizedText(string input, string expected)
    {
        // Arrange — длина результата не превышает длину входа (с учётом trim)
        char[] buffer = new char[input.Length];

        // Act
        int written = StringExtensions.NormalizeWhiteSpaceSpan(input, buffer);

        // Assert
        Assert.Equal(expected, new string(buffer, 0, written));
    }

    [Theory]
    [InlineData("hello  world", 10)]   // на один символ короче
    [InlineData("hello  world", 0)]
    [InlineData("hello", 0)]
    public void NormalizeWhiteSpaceSpan_BufferTooSmall_ThrowsArgumentException(string input, int destSize)
    {
        // Arrange
        char[] buffer = new char[destSize];

        // Act
        // Раньше IndexOutOfRangeException вылетал из цикла записи — уже после того, как
        // часть буфера была заполнена, — и ничто не указывало на размер буфера.
        var ex = Assert.Throws<ArgumentException>(() =>
            StringExtensions.NormalizeWhiteSpaceSpan(input, buffer));

        // Assert
        Assert.Equal("dest", ex.ParamName);
    }

    [Fact]
    public void NormalizeWhiteSpaceSpan_ExactFit_Accepts()
    {
        // Граница: буфер ровно по длине входа — нормализация не может разъехаться.
        const string input = "a  b";
        char[] buffer = new char[input.Length];

        int written = StringExtensions.NormalizeWhiteSpaceSpan(input, buffer);

        Assert.Equal("a b", new string(buffer, 0, written));
    }

    [Fact]
    public void NormalizeWhiteSpaceSpan_EmptyInput_WritesNothingAndSkipsTheBufferCheck()
    {
        char[] buffer = ['x'];

        int written = StringExtensions.NormalizeWhiteSpaceSpan("   ", buffer);

        Assert.Equal(0, written);
        Assert.Equal('x', buffer[0]);
    }

    #endregion

    #region GetMurmurHash3

    [Fact]
    public void GetMurmurHash3_MatchesTheHashOfTheSameTextEncodedToUtf8()
    {
        // Обёртка не должна менять хэш: она лишь кодирует строку в буфер.
        const string input = "The quick brown fox jumps over the lazy dog";

        var viaExtension = input.GetMurmurHash3(seed: 7);
        var viaBytes = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(input), 7);

        Assert.Equal(viaBytes, viaExtension);
    }

    [Fact]
    public void GetMurmurHash3_NonAsciiText_StaysCanonical()
    {
        // Многобайтовые символы: оценка длины буфера в 3 байта на символ должна
        // оставаться достаточной, а результат — совпадать с хэшем самих байтов.
        const string input = "привет мир";

        var viaExtension = input.GetMurmurHash3();
        var viaBytes = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(input), 0);

        Assert.Equal(viaBytes, viaExtension);
    }

    [Fact]
    public void GetMurmurHash3_EmojiOutsideTheStackallocThreshold_UsesTheHeapBuffer()
    {
        // Строка длиннее порога stackalloc идёт через new byte[]; при 4 байтах на символ
        // старая оценка `Length * 3` всё ещё умещалась, поэтому здесь важно совпадение
        // с хэшем байтов, а не сам порог.
        string input = string.Concat(Enumerable.Repeat("😀", 200));

        var viaExtension = input.GetMurmurHash3(seed: 3);
        var viaBytes = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(input), 3);

        Assert.Equal(viaBytes, viaExtension);
    }

    [Fact]
    public void GetMurmurHash3_LongAsciiInput_StaysCanonical()
    {
        // 100_000 символов — заметно за порогом stackalloc, но оценка длины не должна
        // ни переполниться, ни обрезать буфер.
        string input = new('x', 100_000);

        var viaExtension = input.GetMurmurHash3();
        var viaBytes = MurmurHash3.Hash32(Encoding.UTF8.GetBytes(input), 0);

        Assert.Equal(viaBytes, viaExtension);
    }

    #endregion
}
