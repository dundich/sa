using Sa.Configuration.CommandLine;

namespace Sa.ConfigurationTests;


public class ArgumentsTests
{
    [Fact]
    public void TestSingleParameterWithoutValue()
    {
        // Arrange
        var args = new string[] { "-param1" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("true", arguments["param1"]);
    }

    [Fact]
    public void TestSingleParameterWithValue()
    {
        // Arrange
        var args = new string[] { "-param1", "value1" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("value1", arguments["param1"]);
    }

    [Fact]
    public void TestMultipleParameters()
    {
        // Arrange
        var args = new string[] { "-param1", "value1", "--param2", "value2", "param3" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("value1", arguments["param1"]);
        Assert.Equal("value2 param3", arguments["param2"]);
    }

    [Fact]
    public void TestParameterWithEnclosedValue()
    {
        // Arrange
        var args = new string[] { "--param1=\"value with spaces\"" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("value with spaces", arguments["param1"]);
    }

    [Fact]
    public void TestParameterWithQuotes()
    {
        // Arrange
        var args = new string[] { "--param1='quoted value'" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("quoted value", arguments["param1"]);
    }

    [Fact]
    public void TestParameterWithSpecialCharacters()
    {
        // Arrange
        var args = new string[] { "--param1=Test-:-work" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("Test-:-work", arguments["param1"]);
    }

    [Fact]
    public void TestUnknownParameter()
    {
        // Arrange
        var args = new string[] { "unknown" };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Null(arguments["unknown"]); // Should return null for unknown parameters
    }

    /// <summary>
    /// --config_db /opt/service_configs/config_db.json --config_file /opt/service_configs/appsettings.json --config_nlog /opt/service_configs/NLog.config -ip_override $(hostname -i)
    /// </summary>
    [Fact]
    public void TestArgumentsParsing()
    {
        // Arrange
        var args = new string[]
        {
            "--config_db", "/opt/service_configs/config_db.json",
            "--config_file", "/opt/service_configs/appsettings.json",
            "--config_nlog", "/opt/service_configs/NLog.config",
            "-ip_override", "127.0.0.1",
            "-nosjmp"
        };

        // Act
        var arguments = new Arguments(args);

        // Assert
        Assert.Equal("/opt/service_configs/config_db.json", arguments["config_db"]);
        Assert.Equal("/opt/service_configs/appsettings.json", arguments["config_file"]);
        Assert.Equal("/opt/service_configs/NLog.config", arguments["config_nlog"]);
        Assert.Equal("127.0.0.1", arguments["ip_override"]);
        Assert.Equal("true", arguments["nosjmp"]);
    }


    [Fact]
    public void TestSplitToPairsArguments_WithValidInput()
    {
        // Arrange
        var input = new List<string>
        {
            "--config_db", "/opt/service_configs/config_db.json",
            "--config_file", "/opt/service_configs/appsettings.json",
            "--config_nlog", "/opt/service_configs/NLog.config",
            "-ip_override", "$(hostname -i)",
            "-nosjmp", "--param1"
        };

        // Act
        var result = Arguments.SplitToPairs(input);

        // Assert
        var expected = new List<string>
        {
            "--config_db=/opt/service_configs/config_db.json",
            "--config_file=/opt/service_configs/appsettings.json",
            "--config_nlog=/opt/service_configs/NLog.config",
            "-ip_override=$(hostname -i)",
            "-nosjmp=true",
            "--param1=true"
        };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestSplitToPairsArguments_WithMissingValues()
    {
        // Arrange
        var input = new List<string>
        {
            "--config_db", "--config_file", "-ip_override"
        };

        // Act
        var result = Arguments.SplitToPairs(input);

        // Assert
        var expected = new List<string>
        {
            "--config_db=true",
            "--config_file=true",
            "-ip_override=true"
        };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestSplitToPairsArguments_WithNoParameters()
    {
        // Arrange
        var input = new List<string>();

        // Act
        var result = Arguments.SplitToPairs(input);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void TestSplitToPairsArguments_WithSingleParameter()
    {
        // Arrange
        var input = new List<string>
        {
            "--single_param", "value"
        };

        // Act
        var result = Arguments.SplitToPairs(input);

        // Assert
        var expected = new List<string>
        {
            "--single_param=value"
        };
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TestSplitToPairsArguments_WithMultipleValuesAndSimpleParameter()
    {
        // Arrange
        var input = new List<string>
        {
            "--multi_param", "value1", "value2", "--r=2"
        };

        // Act
        var result = Arguments.SplitToPairs(input);

        // Assert
        var expected = new List<string>
        {
            "--multi_param=value1 value2",
            "--r=2"
        };
        Assert.Equal(expected, result);
    }
}