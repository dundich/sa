using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// <c>WithEnabled(false)</c> is a registration switch: the provider records its intents but commits
/// nothing, so no settings, no keyed data source (whose <c>ValidateOnStart</c> would fail on an
/// absent server) and no provider registration reach the container.
/// </summary>
public class PgEnabledTests
{
    [Fact]
    public void Disabled_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(cfg => cfg
            .WithEnabled(false)
            .WithPollingBudget(TimeSpan.FromMinutes(1))
            .WithDataSource()
            .WithOutboxSettings((_, settings) => settings.TableSettings.DatabaseSchemaName = "ignored"));

        // Not even the always-on parts: a disabled provider must be invisible to the container, so
        // a configuration-driven off switch cannot fail host validation.
        Assert.Empty(services);
    }

    [Fact]
    public void Disabled_LeavesNoStartupValidationAndNoProvider()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithEnabled(false));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.Null(provider.GetService<IStartupValidator>());
        Assert.Null(provider.GetService<IOutboxProviderRegistry>());
        Assert.Null(provider.GetService<IOutboxProviderHealth>());
        Assert.Null(provider.GetService<PgOutboxSettings>());
    }

    [Fact]
    public void Enabled_RegistersStartupValidation()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithEnabled(true));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        // The contrast that makes the disabled assertion meaningful: an enabled provider hands its
        // data-source options to the standard startup validator.
        Assert.NotNull(provider.GetService<IStartupValidator>());
        Assert.NotNull(provider.GetService<IOutboxProviderRegistry>());
    }

    [Fact]
    public void OneDisabledAndOneEnabled_OnlyTheEnabledProviderIsRegistered()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithEnabled(false),
            scopeKey: "pg-disabled");

        services.AddSaOutboxUsingPostgreSql(
            cfg => cfg.WithDataSource(),
            scopeKey: "pg-enabled");

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Null(provider.GetKeyedService<IOutboxProviderRegistry>("pg-disabled"));
        Assert.Null(provider.GetKeyedService<PgOutboxSettings>("pg-disabled"));

        Assert.NotNull(provider.GetKeyedService<IOutboxProviderRegistry>("pg-enabled"));
    }
}
