using Microsoft.Extensions.Configuration;
using Sa.Configuration;
using Sa.Configuration.SecretStore;
using Sa.Configuration.Sources;

namespace Sa.ConfigurationTests;

/// <summary>
/// <c>UseStandardSources</c>: the static set for hosts that do not add it themselves, added
/// idempotently so the same call is a no-op in ASP.NET Core.
/// </summary>
public class StandardSourcesTests
{
    private static SecretOptions Secrets()
        => new() { LoadDotEnv = false, Encryption = new SecretEncryptionOptions { Enabled = false } };

    [Fact]
    public void UseStandardSources_AddsJsonFiles_EnvironmentVariablesAndArguments()
    {
        using TempDirectory temp = new();
        temp.Write("appsettings.json", """{ "theme": "dark", "only_base": "base" }""");
        temp.Write("appsettings.Development.json", """{ "theme": "light" }""");

        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa =>
        {
            sa.UseStandardSources(options =>
            {
                options.BasePath = temp.Path;
                options.EnvironmentName = "Development";
                options.Optional = false;
                options.ReloadOnChange = false;
            });
            sa.UseSecrets(Secrets());
        });

        IConfiguration configuration = builder.Build();

        // appsettings.{environment}.json is added after appsettings.json, so it wins.
        Assert.Equal("light", configuration["theme"]);
        Assert.Equal("base", configuration["only_base"]);
    }

    [Fact]
    public void UseStandardSources_IsIdempotent()
    {
        using TempDirectory temp = new();
        temp.Write("appsettings.json", """{ "theme": "dark" }""");

        var builder = new ConfigurationBuilder();
        builder.AddSaConfiguration(sa =>
        {
            sa.UseStandardSources(options =>
            {
                options.BasePath = temp.Path;
                options.EnvironmentName = "Development";
                options.Optional = true;
                options.ReloadOnChange = false;
            });
        });

        int before = builder.Sources.Count;
        SaConfigurationBuilder.Create(builder).UseStandardSources(options =>
        {
            options.BasePath = temp.Path;
            options.EnvironmentName = "Development";
            options.Optional = true;
            options.ReloadOnChange = false;
        });

        // A second call must not add a second file watcher or a second environment scan.
        Assert.Equal(before, builder.Sources.Count);
        Assert.Equal("dark", builder.Build()["theme"]);
    }

    [Fact]
    public void UseStandardSources_SkipsSourcesTheHostAlreadyAdded()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();

        int before = builder.Sources.Count;
        SaConfigurationBuilder.Create(builder).UseStandardSources(options =>
        {
            options.IncludeCommandLineArguments = false;
            options.EnvironmentName = "Production";
            options.JsonFiles = ["appsettings.json"];      // the only file the host already added
        });

        Assert.Equal(before, builder.Sources.Count);
    }
}
