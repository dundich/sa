﻿using Sa.Classes;
using Sa.Extensions;

namespace SaTests.Classes;

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

        // Act
        string result = strValue.ToString();

        // Assert
        Assert.Equal("s:hello", result);
    }

    [Fact]
    public void ToString_ConvertsChoiceNum_ToStringWithPrefix()
    {
        // Arrange
        StrOrNum numValue = 42;

        // Act
        string result = numValue.ToString();

        // Assert
        Assert.Equal("n:42", result);
    }

    [Fact]
    public void Parse_ConvertsStringBackToChoiceStr()
    {
        // Arrange
        string input = "s:world";

        // Act
        StrOrNum parsed = StrOrNum.Parse(input);

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
        StrOrNum parsed = StrOrNum.Parse(input);

        // Assert
        Assert.IsType<StrOrNum.ChoiceNum>(parsed);
        Assert.Equal(123, parsed);
    }

    [Fact]
    public void Parse_EmptyString()
    {
        // Arrange
        string input = "";
        Assert.Equal("", StrOrNum.Parse(input));
    }

}