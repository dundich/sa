using Sa.Extensions;

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
}