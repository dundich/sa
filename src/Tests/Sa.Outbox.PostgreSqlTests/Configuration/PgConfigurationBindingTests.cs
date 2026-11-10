using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests of the configuration-simplification surface: the
/// <c>Outbox:DeliveryGroups:{group}</c> convention, single-call registration, fail-fast budget
/// validation and the two new configuration-section binders
/// (<see cref="PgPollingOptions"/> / <see cref="PgOutboxOptions"/>).
/// </summary>
public class PgConfigurationBindingTests
{
    private const string GroupId = "bind_grp";

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    internal sealed class BindingConsumer : IConsumer<BindingMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<BindingMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class BindingMessage
    {
        public int TenantId { get; set; }
    }

    #region OutboxDeliveryConfiguration

    [Fact]
    public void Convention_DefaultPrefix_ResolvesTheGroupSection()
    {
        var convention = new OutboxDeliveryConfiguration();

        Assert.Equal(OutboxDeliveryConfiguration.DefaultSectionPrefix, convention.SectionPrefix);
        Assert.Equal("Outbox:DeliveryGroups:orders", convention.ResolveSection("orders"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Convention_NoPrefix_IsDisabled(string? prefix)
    {
        var convention = new OutboxDeliveryConfiguration(prefix);

        Assert.Null(convention.SectionPrefix);
        Assert.Null(convention.ResolveSection("orders"));
    }

    [Fact]
    public void Convention_TrailingColon_IsNormalized()
    {
        var convention = new OutboxDeliveryConfiguration("Custom:Groups:");

        Assert.Equal("Custom:Groups", convention.SectionPrefix);
        Assert.Equal("Custom:Groups:orders", convention.ResolveSection("orders"));
    }

    [Fact]
    public void Convention_Builder_ReplacesTheDefault()
    {
        var services = new ServiceCollection();
        OutboxBuilder.Create(services, OutboxScope.DefaultKey)
            .WithDeliveryConfigurationSection("Custom:Groups");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal("Custom:Groups", provider.GetRequiredService<OutboxDeliveryConfiguration>().SectionPrefix);
    }

    [Fact]
    public void Convention_Builder_Null_DisablesIt()
    {
        var services = new ServiceCollection();
        OutboxBuilder.Create(services, OutboxScope.DefaultKey)
            .WithDeliveryConfigurationSection(null);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<OutboxDeliveryConfiguration>().ResolveSection("orders"));
    }

    #endregion

    #region Single-call registration

    [Fact]
    public void AddSaOutboxUsingPostgreSql_OneCall_RegistersDeliveriesAndPollingDefaults()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(
            pg => pg.WithPollingBudget(TimeSpan.FromMinutes(1)),
            outbox => outbox.WithDeliveries(d => d
                .AddDelivery<BindingConsumer, BindingMessage>(GroupId)));

        using ServiceProvider provider = services.BuildServiceProvider();

        OutboxConsumerSettings? settings = provider.GetRequiredService<IOutboxConsumerManager>().GetSettings(GroupId);

        Assert.NotNull(settings);
        // The polling defaults came through the same call…
        Assert.Equal(TimeSpan.FromSeconds(45), settings.IdleMaxInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), settings.Interval);
    }

    #endregion

    #region Provider descriptor

    [Fact]
    public void Provider_PostgreSql_RegistersDescriptorWithCapabilities()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql();

        OutboxProviderRegistration registration = Assert.Single(
            services
                .Where(d => d.ServiceType == typeof(OutboxProviderRegistration))
                .Select(d => (OutboxProviderRegistration)d.ImplementationInstance!));

        Assert.Equal("PostgreSql", registration.Descriptor.Name);
        Assert.True(registration.Descriptor.Supports(
            OutboxProviderCapabilities.PublishContext
            | OutboxProviderCapabilities.TenantDetection
            | OutboxProviderCapabilities.LockExtension
            | OutboxProviderCapabilities.Postponement));
    }

    [Fact]
    public void Provider_Registry_ValidatesTheFullPostgreSqlInstallation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // No database is contacted: the registry validates the *registrations* only.
        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource(ds => ds.Options(options =>
            options.Configure(o => o.ConnectionString =
                "Host=localhost;Port=1;Database=outbox;Username=u;Password=p;Timeout=1;Pooling=false"))));

        using ServiceProvider provider = services.BuildServiceProvider();

        IOutboxProviderRegistry registry = provider.GetRequiredService<IOutboxProviderRegistry>();

        Assert.Equal("PostgreSql", registry.Descriptor.Name);
        Assert.True(registry.Descriptor.Supports(OutboxProviderCapabilities.PublishContext));

        // Registration-level checks: the registry validates without constructing the storage chain.
        IServiceProviderIsService isService = provider.GetRequiredService<IServiceProviderIsService>();
        Assert.True(isService.IsService(typeof(IOutboxBulkWriter)));
        Assert.True(isService.IsService(typeof(IOutboxDeliveryManager)));
        Assert.True(isService.IsService(typeof(IOutboxTenantDetector)));
    }

    #endregion

    #region Fail-fast validation

    [Fact]
    public void WithPollingBudget_TooSmall_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        var configuration = new PgOutboxConfiguration(services);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => configuration.WithPollingBudget(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WithPollingProfile_Undefined_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        var configuration = new PgOutboxConfiguration(services);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => configuration.WithPollingProfile((PgPollingProfile)42));
    }

    #endregion

    #region WithPollingFromConfiguration

    [Fact]
    public void PollingFromConfiguration_BindsTheProfile()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Sa:Outbox:Pg:Polling:Budget"] = "00:01:00",
            ["Sa:Outbox:Pg:Polling:BatchingWindow"] = "00:00:02",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithPollingFromConfiguration("Sa:Outbox:Pg:Polling").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        PgPollingSettings polling = provider.GetRequiredService<PgPollingSettings>();
        Assert.True(polling.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(2), polling.BatchingWindow);

        // 60 s budget − BatchingWindow(2 s) − reserve(12 s) = 46 s, because the explicit window is
        // applied before the budget is computed.
        Assert.Equal(TimeSpan.FromSeconds(46), polling.IdleMaxInterval);

        // Interval = idle / 3 (integer ticks), so it lands between 15 and 16 seconds.
        Assert.InRange(polling.Interval, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(16));
    }

    [Fact]
    public void PollingFromConfiguration_ExplicitValuesWinOverTheProfile()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Polling:Profile"] = "Quiet",
            ["Polling:Interval"] = "00:00:05",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithPollingFromConfiguration("Polling").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        PgPollingSettings polling = provider.GetRequiredService<PgPollingSettings>();
        Assert.Equal(TimeSpan.FromSeconds(5), polling.Interval);      // explicit
        Assert.Equal(TimeSpan.FromSeconds(45), polling.IdleMaxInterval); // from Quiet
    }

    [Fact]
    public void PollingFromConfiguration_Disabled_LeavesTheLayerOff()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Polling:Enabled"] = "false",
            ["Polling:Profile"] = "Balanced",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithPollingFromConfiguration("Polling").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<PgPollingSettings>().Enabled);
    }

    [Fact]
    public void PollingFromConfiguration_MissingSectionOrConfiguration_IsNoOp()
    {
        var servicesWithoutConfig = new ServiceCollection();
        new PgOutboxConfiguration(servicesWithoutConfig).WithPollingFromConfiguration("Polling").Commit();

        using (ServiceProvider provider = servicesWithoutConfig.BuildServiceProvider())
        {
            // No IConfiguration in the container — the layer is registered but stays disabled.
            Assert.False(provider.GetRequiredService<PgPollingSettings>().Enabled);
        }

        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Other:Profile"] = "Balanced",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithPollingFromConfiguration("Polling").Commit();

        using ServiceProvider withConfig = services.BuildServiceProvider();
        Assert.False(withConfig.GetRequiredService<PgPollingSettings>().Enabled);
    }

    #endregion

    #region WithOutboxSettingsFromConfiguration

    [Fact]
    public void OutboxSettingsFromConfiguration_BindsScalars()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Sa:Outbox:Pg:Schema"] = "messaging",
            ["Sa:Outbox:Pg:UseDefaultPartition"] = "true",
            ["Sa:Outbox:Pg:Tables:TaskQueue:TableName"] = "orders_q",
            ["Sa:Outbox:Pg:Tables:TaskQueue:FillFactor"] = "70",
            ["Sa:Outbox:Pg:Tables:Message:TableName"] = "orders_msg$",
            ["Sa:Outbox:Pg:Migration:ForwardDays"] = "3",
            ["Sa:Outbox:Pg:Migration:ExecutionInterval"] = "06:00:00",
            ["Sa:Outbox:Pg:Cleanup:DropPartsAfterRetention"] = "14.00:00:00",
            ["Sa:Outbox:Pg:Cleanup:CleanupMode"] = "Archive",
            ["Sa:Outbox:Pg:Cleanup:ArchiveSchemaName"] = "cold",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithOutboxSettingsFromConfiguration("Sa:Outbox:Pg").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();
        PgOutboxSettings settings = provider.GetRequiredService<PgOutboxSettings>();

        Assert.Equal("messaging", settings.TableSettings.DatabaseSchemaName);
        Assert.True(settings.TableSettings.UseDefaultPartition);
        Assert.Equal("orders_q", settings.TableSettings.TaskQueue.TableName);
        Assert.Equal(70, settings.TableSettings.TaskQueue.FillFactor);
        Assert.Equal("orders_msg$", settings.TableSettings.Message.TableName);
        Assert.Equal(3, settings.MigrationSettings.ForwardDays);
        Assert.Equal(TimeSpan.FromHours(6), settings.MigrationSettings.ExecutionInterval);
        Assert.Equal(TimeSpan.FromDays(14), settings.CleanupSettings.DropPartsAfterRetention);
        Assert.Equal(Sa.Partitional.PostgreSql.PartCleanupMode.Archive, settings.CleanupSettings.CleanupMode);
        Assert.Equal("cold", settings.CleanupSettings.ArchiveSchemaName);
    }

    [Fact]
    public void OutboxSettingsFromConfiguration_UnnamedValuesKeepTheirCodeDefaults()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Pg:Schema"] = "messaging",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithOutboxSettingsFromConfiguration("Pg").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();
        PgOutboxSettings settings = provider.GetRequiredService<PgOutboxSettings>();

        Assert.Equal("messaging", settings.TableSettings.DatabaseSchemaName);
        Assert.Equal(PgOutboxTableSettings.Defaults.DatabaseTableName, settings.TableSettings.TaskQueue.TableName);
        Assert.Equal(TimeSpan.FromDays(30), settings.CleanupSettings.DropPartsAfterRetention);
    }

    [Fact]
    public void OutboxSettingsFromConfiguration_MissingSection_IsNoOp()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Other:Schema"] = "messaging",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithOutboxSettingsFromConfiguration("Pg").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();
        PgOutboxSettings settings = provider.GetRequiredService<PgOutboxSettings>();

        Assert.Equal(PgOutboxTableSettings.Defaults.DatabaseSchemaName, settings.TableSettings.DatabaseSchemaName);
    }

    [Fact]
    public void OutboxSettingsFromConfiguration_InvalidFillFactor_FailsAtResolution()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Pg:Tables:TaskQueue:FillFactor"] = "500",
        });

        var services = new ServiceCollection();
        services.AddSingleton(configuration);

        new PgOutboxConfiguration(services).WithOutboxSettingsFromConfiguration("Pg").Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.GetRequiredService<PgOutboxSettings>());
    }

    [Fact]
    public void OutboxSettingsFromConfiguration_BlankSectionPath_Throws()
    {
        var configuration = new PgOutboxConfiguration(new ServiceCollection());

        Assert.Throws<ArgumentException>(() => configuration.WithOutboxSettingsFromConfiguration("  "));
    }

    #endregion
}
