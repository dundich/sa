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

    /// <summary>
    /// CLI arguments follow the environment-variable convention: <c>--sa__pg__host</c> exposes
    /// the <c>sa:pg:host</c> configuration key, and both spellings resolve through the provider.
    /// </summary>
    [Fact]
    public void Load_TreatsDoubleUnderscoreAsConfigurationSeparator()
    {
        // Arrange
        var source = new ArgumentsConfigurationSource
        {
            Args = ["--sa__pg__host=db", "--theme=dark"]
        };
        var provider = new ArgumentsConfigurationProvider(source);

        // Act
        provider.Load();

        // Assert — canonical storage…
        Assert.True(provider.TryGet("sa:pg:host", out var host));
        Assert.Equal("db", host);

        // …and the alias answers for it on read, so consumers may use either spelling.
        Assert.True(provider.TryGet("sa__pg__host", out var aliasHost));
        Assert.Equal("db", aliasHost);

        // Ordinary keys are unaffected.
        Assert.True(provider.TryGet("theme", out var theme));
        Assert.Equal("dark", theme);
    }
}
