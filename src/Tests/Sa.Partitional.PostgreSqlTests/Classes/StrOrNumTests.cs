using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Partitional.PostgreSqlTests.Classes;

public class StrOrNumTests
{
    [Fact]
    public void StrOrNum_Mustbe_Serialize_json()
    {
        StrOrNum expected = "{Hi}\"";

        string json = expected.ToJson();

        Assert.NotEmpty(json);

        StrOrNum actual = json.FromJson<StrOrNum>()!;

        Assert.Equal(expected, actual);


        expected = 123;

        json = expected.ToJson();

        Assert.NotEmpty(json);

        actual = json.FromJson<StrOrNum>()!;

        Assert.Equal(expected, actual);
    }


    [Fact]
    public void ToString_ConvertsChoiceStr_ToStringWithPrefix()
    {
        // Arrange
        StrOrNum strValue = "hello";

        string str = strValue.ToString();

        // Assert
        Assert.Equal("hello", str);


        str = strValue.ToFmtString();
        Assert.Equal("s:hello", str);
    }

    [Fact]
    public void ToString_ConvertsChoiceNum_ToStringWithPrefix()
    {
        // Arrange
        StrOrNum numValue = 42;

        // Act
        string result = numValue.ToString();
        Assert.Equal("42", result);

        result = numValue.ToFmtString();
        // Assert
        Assert.Equal("n:42", result);
    }

    [Fact]
    public void Parse_ConvertsStringBackToChoiceStr()
    {
        // Arrange
        string input = "s:world";

        // Act
        StrOrNum parsed = StrOrNum.FromFmtStr(input);

        // Assert
        Assert.IsType<StrOrNum.ChoiceStr>(parsed);
        Assert.Equal("world", parsed);
    }

    [Fact]
    public void Parse_ConvertsStringBackToChoiceNum()
    {
        // Arrange
        string input = "n:123";

        // Act
        StrOrNum parsed = StrOrNum.FromFmtStr(input);

        // Assert
        Assert.IsType<StrOrNum.ChoiceNum>(parsed);
        Assert.Equal(123, parsed);
    }

    [Fact]
    public void Parse_EmptyString()
    {
        // Arrange
        string input = "";
        Assert.Equal("", StrOrNum.FromFmtStr(input));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("with,comma")]
    [InlineData("with\\backslash")]
    [InlineData("comma,backslash\\both")]
    [InlineData("s:looks-like-a-prefix")]
    [InlineData("n:123")]
    [InlineData(",leading")]
    [InlineData("trailing,")]
    [InlineData("")]
    [InlineData("\\\\")]
    [InlineData("\\,")]
    public void RoundTrip_StringValue_SurvivesTheSeparator(string value)
    {
        // Several values share one column, joined by a comma, so a comma inside a value used to come
        // back as two values and stop matching the request that created the partition.
        StrOrNum original = new StrOrNum.ChoiceStr(value);

        StrOrNum parsed = StrOrNum.FromFmtStr(original.ToFmtString());

        Assert.Equal(new StrOrNum.ChoiceStr(value), parsed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void RoundTrip_NumberValue_IsUnchanged(long value)
    {
        StrOrNum original = value;

        StrOrNum parsed = StrOrNum.FromFmtStr(original.ToFmtString());

        Assert.Equal(new StrOrNum.ChoiceNum(value), parsed);
    }

    [Fact]
    public void ToFmtString_EscapesTheSeparatorAndTheEscapeCharacter()
    {
        Assert.Equal(@"s:a\,b", new StrOrNum.ChoiceStr("a,b").ToFmtString());
        Assert.Equal(@"s:a\\b", new StrOrNum.ChoiceStr(@"a\b").ToFmtString());
        Assert.Equal("n:42", new StrOrNum.ChoiceNum(42).ToFmtString());
    }

    [Fact]
    public void RoundTrip_ThroughJson_KeepsTheSeparator()
    {
        StrOrNum original = new StrOrNum.ChoiceStr("a,b");

        StrOrNum actual = original.ToJson().FromJson<StrOrNum>()!;

        Assert.Equal(original, actual);
    }

    [Theory]
    [InlineData("hello", 0L)]
    [InlineData("42", 42L)]
    [InlineData("not-a-number", 0L)]
    public void ExplicitConversion_FromStringToLong_KeepsItsDocumentedFallback(string value, long expected)
    {
        // Documented behaviour, pinned here so that changing the silent 0 fallback becomes a
        // deliberate decision: the conversion renders a partition value, and a silent 0 would be
        // indistinguishable from a real zero.
        StrOrNum choice = value;

        Assert.Equal(expected, (long)choice);
    }
}
