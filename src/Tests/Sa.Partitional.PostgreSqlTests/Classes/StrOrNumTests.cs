using Sa.Partitional.PostgreSql.Classes;
using Sa.Partitional.PostgreSql.SqlBuilder;

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

    [Fact]
    public void ExplicitConversion_FromNumberToInt_OverflowIsNotSilent()
    {
        // Narrowing a long used to wrap around: a partition value 2^32+1 became 1 without a word.
        StrOrNum choice = (long)int.MaxValue + 1;

        Assert.Throws<OverflowException>(() => { _ = (int)choice; });
        Assert.Throws<OverflowException>(() => { _ = (short)choice; });
    }

    /// <summary>
    /// The cache table stores a whole value set in one <c>part_values</c> string, joined by a comma,
    /// with <c>\</c> as the escape. Everything that reaches a partition key — including text an
    /// application does not control — travels through this encoding, so it has to survive the round
    /// trip byte for byte, and a value that contains a comma must stay one value.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("a,b")]
    [InlineData(@"a\b")]
    [InlineData(@"a\")]
    [InlineData(@"\")]
    [InlineData(@"\\")]
    [InlineData(@"\,\,")]
    [InlineData("s:n:1")]
    [InlineData("кириллица")]
    [InlineData("a b\tc")]
    [InlineData(" , ")]
    public void PartValues_RoundTripThroughTheCacheFormat(string value)
    {
        StrOrNum[] values = [7, value, "tail"];

        string encoded = string.Join(',', values.Select(v => v.ToFmtString()));
        StrOrNum[] decoded = SqlTemplate.ParseStrOrNums(encoded);

        Assert.Equal(values, decoded);
    }

    [Fact]
    public void PartValues_SeparatorInsideAValue_DoesNotSplitIt()
    {
        StrOrNum[] values = [new StrOrNum.ChoiceStr("a,b"), new StrOrNum.ChoiceStr("c,d")];

        string encoded = string.Join(',', values.Select(v => v.ToFmtString()));
        StrOrNum[] decoded = SqlTemplate.ParseStrOrNums(encoded);

        Assert.Equal(2, decoded.Length);
        Assert.Equal(values, decoded);
    }

    [Fact]
    public void PartValues_EmptySet_StaysEmpty()
    {
        Assert.Empty(SqlTemplate.ParseStrOrNums(string.Empty));
    }
}
