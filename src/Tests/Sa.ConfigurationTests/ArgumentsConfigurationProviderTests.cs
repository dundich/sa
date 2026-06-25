using Sa.Configuration.CommandLine;

namespace Sa.ConfigurationTests;

public class ArgumentsConfigurationProviderTests
{
    [Fact]
    public void Load_PopulatesData_FromArguments()
    {
        // Arrange
        var source = new ArgumentsConfigurationSource()
        {
            Args = ["--host", "localhost", "--port", "5432", "--debug"]
        };
        var provider = new ArgumentsConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert
        Assert.True(provider.TryGet("host", out var l));
        Assert.True(provider.TryGet("port", out var p));
        Assert.True(provider.TryGet("debug", out var d));

        Assert.Equal("localhost", l);
        Assert.Equal("5432", p);
        Assert.Equal("true", d);
    }


    [Fact]
    public void Load_HandlesEqualsSeparator()
    {
        // Arrange
        var source = new ArgumentsConfigurationSource
        {
            Args = ["--connection_string=Host=db;Port=5432"]
        };
        var provider = new ArgumentsConfigurationProvider(source);

        // Act
        provider.Load();

        Assert.True(provider.TryGet("connection_string", out var r));
        // Assert
        Assert.Equal("Host=db;Port=5432", r);
    }

    [Fact]
    public void Load_HandlesMixedSeparators()
    {
        // Arrange
        var source = new ArgumentsConfigurationSource
        {
            Args = ["--host=localhost", "-port", "5432", "--debug"]
        };
        var provider = new ArgumentsConfigurationProvider(source);

        // Act
        provider.Load();



        // Assert
        Assert.True(provider.TryGet("host", out var r1));
        Assert.True(provider.TryGet("port", out var r2));
        Assert.True(provider.TryGet("debug", out var r3));


        Assert.Equal("localhost", r1);
        Assert.Equal("5432", r2);
        Assert.Equal("true", r3);
    }
}
