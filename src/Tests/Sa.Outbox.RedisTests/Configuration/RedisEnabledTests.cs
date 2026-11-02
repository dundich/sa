using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis;
using Sa.Outbox.Redis.Configuration;

namespace Sa.Outbox.RedisTests.Configuration;

/// <summary>
/// <c>WithEnabled(false)</c> is a registration switch: the provider records its intents but commits
/// nothing, so no settings, no connection and no provider registration reach the container.
/// </summary>
public class RedisEnabledTests
{
    [Fact]
    public void Disabled_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(cfg => cfg
            .WithEnabled(false)
            .WithConnectionString("localhost:6379")
            .WithConfiguration("Outbox:Redis")
            .WithOutboxSettings((_, settings) => settings.KeySettings.Prefix = "ignored"));

        // Not even the always-on parts: a disabled provider must be invisible to the container, so
        // a configuration-driven off switch cannot fail host validation.
        Assert.Empty(services);
    }

    [Fact]
    public void Disabled_LeavesNoProviderAndNoConnection()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(cfg => cfg.WithEnabled(false));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Null(provider.GetService<IOutboxProviderRegistry>());
        Assert.Null(provider.GetService<RedisOutboxSettings>());
        Assert.Null(provider.GetKeyedService<StackExchange.Redis.IDatabase>(OutboxScope.DefaultKey));
    }

    [Fact]
    public void Enabled_RegistersTheProvider()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(cfg => cfg.WithEnabled(true).WithConnectionString("localhost:6379"));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetService<IOutboxProviderRegistry>());
        Assert.NotNull(provider.GetService<RedisOutboxSettings>());
    }

    [Fact]
    public void OneDisabledAndOneEnabled_OnlyTheEnabledProviderIsRegistered()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingRedis(
            cfg => cfg.WithEnabled(false).WithConnectionString("localhost:6379"),
            scopeKey: "redis-disabled");

        services.AddSaOutboxUsingRedis(
            cfg => cfg.WithConnectionString("localhost:6379"),
            scopeKey: "redis-enabled");

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Null(provider.GetKeyedService<IOutboxProviderRegistry>("redis-disabled"));
        Assert.Null(provider.GetKeyedService<RedisOutboxSettings>("redis-disabled"));

        Assert.NotNull(provider.GetKeyedService<IOutboxProviderRegistry>("redis-enabled"));
    }
}
