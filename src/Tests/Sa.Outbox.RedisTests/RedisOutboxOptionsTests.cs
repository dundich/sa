using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;

namespace Sa.Outbox.RedisTests;

/// <summary>
/// The Redis settings are a standard <c>Microsoft.Extensions.Options</c> instance: configuration is
/// bound and overrides the <c>WithOutboxSettings</c> code defaults, <c>Options(...)</c> actions
/// override both, and validation runs when the settings are first resolved.
/// </summary>
public class RedisOutboxOptionsTests
{
    private const string Section = "Outbox:Redis";

    [Fact]
    public void Configuration_BindsTheWholeSettingsTree()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{Section}:KeySettings:Prefix"] = "from-config",
            [$"{Section}:ConsumeSettings:PendingClaimIdle"] = "00:05:00",
            [$"{Section}:ConsumeSettings:MaxDeliveryAttempts"] = "7",
            [$"{Section}:ConsumeSettings:StartFrom"] = "Beginning",
            [$"{Section}:RetentionSettings:Enabled"] = "true",
            [$"{Section}:RetentionSettings:StreamMaxAge"] = "1.00:00:00",
            [$"{Section}:RetentionSettings:TrimInterval"] = "00:02:00",
            [$"{Section}:RetentionSettings:Approximate"] = "false",
            [$"{Section}:RetentionSettings:TrimLimit"] = "0",
        });

        using ServiceProvider provider = BuildProvider(
            configuration,
            cfg => cfg.WithConfiguration(Section));

        RedisOutboxSettings settings = provider.GetRequiredService<RedisOutboxSettings>();

        Assert.Equal("from-config", settings.KeySettings.Prefix);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.ConsumeSettings.PendingClaimIdle);
        Assert.Equal(7, settings.ConsumeSettings.MaxDeliveryAttempts);
        Assert.Equal(RedisOutboxGroupStart.Beginning, settings.ConsumeSettings.StartFrom);

        Assert.True(settings.RetentionSettings.Enabled);
        Assert.Equal(TimeSpan.FromDays(1), settings.RetentionSettings.StreamMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.RetentionSettings.TrimInterval);
        Assert.False(settings.RetentionSettings.Approximate);
        Assert.Equal(0, settings.RetentionSettings.TrimLimit);
    }

    [Fact]
    public void Precedence_IsCodeThenConfigurationThenOptionsActions()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{Section}:KeySettings:Prefix"] = "from-config",
        });

        using ServiceProvider provider = BuildProvider(
            configuration,
            cfg => cfg
                .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "from-code")
                .WithConfiguration(Section)
                .Options(options => options.Configure(settings => settings.KeySettings.Separator = "|")));

        RedisOutboxSettings settings = provider.GetRequiredService<RedisOutboxSettings>();

        // Configuration wins over the code default...
        Assert.Equal("from-config", settings.KeySettings.Prefix);
        // ...and the Options action still has the last word over both.
        Assert.Equal("|", settings.KeySettings.Separator);
    }

    [Fact]
    public void OptionsActions_RunAfterConfigurationForTheSameKey()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{Section}:KeySettings:Prefix"] = "from-config",
        });

        using ServiceProvider provider = BuildProvider(
            configuration,
            cfg => cfg
                .WithConfiguration(Section)
                .Options(options => options.Configure(settings => settings.KeySettings.Prefix = "from-options")));

        RedisOutboxSettings settings = provider.GetRequiredService<RedisOutboxSettings>();

        Assert.Equal("from-options", settings.KeySettings.Prefix);
    }

    [Fact]
    public void NoConfiguration_KeepsTheCodeDefaults()
    {
        using ServiceProvider provider = BuildProvider(
            BuildConfiguration(new Dictionary<string, string?>()),
            cfg => cfg.WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "code-only"));

        RedisOutboxSettings settings = provider.GetRequiredService<RedisOutboxSettings>();

        Assert.Equal("code-only", settings.KeySettings.Prefix);
        Assert.False(settings.RetentionSettings.Enabled);
        Assert.Equal(RedisOutboxRetentionSettings.Defaults.StreamMaxAge, settings.RetentionSettings.StreamMaxAge);
    }

    [Fact]
    public void InvalidRetention_FailsValidationWhenResolved()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{Section}:RetentionSettings:TrimInterval"] = "00:00:00",
        });

        using ServiceProvider provider = BuildProvider(
            configuration,
            cfg => cfg.WithConfiguration(Section));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<RedisOutboxSettings>());

        Assert.Contains(nameof(RedisOutboxRetentionSettings.TrimInterval), exception.Message);
    }

    [Fact]
    public void TrimLimitWithoutApproximate_FailsValidationWhenResolved()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            [$"{Section}:RetentionSettings:Approximate"] = "false",
            [$"{Section}:RetentionSettings:TrimLimit"] = "1000",
        });

        using ServiceProvider provider = BuildProvider(
            configuration,
            cfg => cfg.WithConfiguration(Section));

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<RedisOutboxSettings>());

        Assert.Contains(nameof(RedisOutboxRetentionSettings.TrimLimit), exception.Message);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildProvider(
        IConfiguration configuration,
        Action<IRedisOutboxConfiguration> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSaOutboxUsingRedis(configure);

        return services.BuildServiceProvider();
    }
}
