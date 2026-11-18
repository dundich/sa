using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests of <see cref="PgPollingSettings"/> (presets, budget arithmetic, validation, application
/// to the builder) and of its two integration points: the delivery-options provider precedence and
/// the DI registration performed by <c>WithPolling</c> / <c>WithPollingBudget</c>.
/// </summary>
public class PgPollingSettingsTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    #region UseBudget

    [Fact]
    public void UseBudget_OneMinute_LeavesMarginAndBoundsTheWorstCase()
    {
        var polling = new PgPollingSettings().UseBudget(TimeSpan.FromMinutes(1));

        Assert.True(polling.Enabled);
        // 60 s − BatchingWindow(3 s) − reserve(12 s) = 45 s of idle waiting.
        Assert.Equal(TimeSpan.FromSeconds(45), polling.IdleMaxInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), polling.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), polling.InitialDelay);
        Assert.Equal(-1, polling.MaxProcessingIterations);

        // The contract the caller asked for: publication → start of processing stays inside the budget
        // even for a fully idle group, with the core batching window included.
        Assert.True(OutboxDefaults.BatchingWindow + polling.IdleMaxInterval <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void UseBudget_ThirtySeconds_ScalesEveryValueDown()
    {
        var polling = new PgPollingSettings().UseBudget(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(21), polling.IdleMaxInterval); // 30 − 3 − 6
        Assert.Equal(TimeSpan.FromSeconds(7), polling.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), polling.InitialDelay);
    }

    [Fact]
    public void UseBudget_ReserveIsAtLeastFiveSeconds()
    {
        // 10 s budget: 20% would be 2 s, the floor is 5 s.
        var polling = new PgPollingSettings().UseBudget(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(2), polling.IdleMaxInterval); // 10 − 3 − 5
    }

    [Fact]
    public void UseBudget_NeverProducesAnIntervalBelowOneSecond()
    {
        var polling = new PgPollingSettings().UseBudget(TimeSpan.FromSeconds(9));

        Assert.Equal(TimeSpan.FromSeconds(1), polling.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), polling.InitialDelay);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-30)]
    public void UseBudget_TooSmall_Throws(int seconds)
    {
        var polling = new PgPollingSettings();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => { polling.UseBudget(TimeSpan.FromSeconds(seconds)); });
    }

    [Fact]
    public void UseBudget_HonoursTheConfiguredBatchingWindow()
    {
        var polling = new PgPollingSettings().WithBatchingWindow(TimeSpan.FromSeconds(10));

        polling.UseBudget(TimeSpan.FromMinutes(1));

        // 60 − 10 − 12 = 38 s, not the 45 s computed from the default 3 s window.
        Assert.Equal(TimeSpan.FromSeconds(38), polling.IdleMaxInterval);
    }

    #endregion

    #region UseProfile

    [Theory]
    [InlineData(PgPollingProfile.Quiet)]
    [InlineData(PgPollingProfile.Balanced)]
    [InlineData(PgPollingProfile.Responsive)]
    public void UseProfile_EveryProfileFitsOneMinuteAndKeepsItsInvariants(PgPollingProfile profile)
    {
        var polling = new PgPollingSettings().UseProfile(profile);

        Assert.True(polling.Enabled);
        Assert.True(polling.Interval > TimeSpan.Zero);
        Assert.True(polling.Interval <= polling.IdleMaxInterval);
        Assert.True(polling.InitialDelay <= polling.Interval);
        Assert.Equal(-1, polling.MaxProcessingIterations);
        Assert.True(OutboxDefaults.BatchingWindow + polling.IdleMaxInterval <= TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(PgPollingProfile.Quiet, 30, 45)]
    [InlineData(PgPollingProfile.Balanced, 10, 30)]
    [InlineData(PgPollingProfile.Responsive, 3, 15)]
    public void UseProfile_UsesTheDocumentedValues(PgPollingProfile profile, int intervalSeconds, int idleSeconds)
    {
        var polling = new PgPollingSettings().UseProfile(profile);

        Assert.Equal(TimeSpan.FromSeconds(intervalSeconds), polling.Interval);
        Assert.Equal(TimeSpan.FromSeconds(idleSeconds), polling.IdleMaxInterval);
    }

    [Fact]
    public void UseProfile_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => { new PgPollingSettings().UseProfile((PgPollingProfile)42); });
    }

    #endregion

    #region ApplyTo

    [Fact]
    public void ApplyTo_DefaultInstance_AppliesNothing()
    {
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        bool applied = new PgPollingSettings().ApplyTo(builder);

        Assert.False(applied);

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(OutboxDefaults.Interval, settings.Interval);
        Assert.Equal(OutboxDefaults.IdleMaxInterval, settings.IdleMaxInterval);
        Assert.Equal(OutboxDefaults.MaxProcessingIterations, settings.MaxProcessingIterations);
    }

    [Fact]
    public void ApplyTo_EnabledProfile_SetsTheWholePollingSurface()
    {
        PgPollingSettings polling = new PgPollingSettings()
            .UseProfile(PgPollingProfile.Balanced)
            .WithBatchingWindow(TimeSpan.FromSeconds(1))
            .WithPerTenantParallelism(4);

        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.True(polling.ApplyTo(builder));

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(TimeSpan.FromSeconds(10), settings.Interval);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.IdleMaxInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.InitialDelay);
        Assert.Equal(-1, settings.MaxProcessingIterations);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.BatchingWindow);
        Assert.Equal(4, settings.PerTenantMaxDegreeOfParallelism);
    }

    [Fact]
    public void ApplyTo_DoesNotTouchUnrelatedSettings()
    {
        var builder = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("grp")
            .WithMaxBatchSize(64)
            .WithLockDuration(TimeSpan.FromSeconds(20));

        new PgPollingSettings().UseProfile().ApplyTo(builder);

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(64, settings.MaxBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.LockDuration);
        Assert.Equal(OutboxDefaults.BatchingWindow, settings.BatchingWindow);
        Assert.Equal(OutboxDefaults.PerTenantMaxDegreeOfParallelism, settings.PerTenantMaxDegreeOfParallelism);
    }

    [Fact]
    public void ApplyTo_ZeroIdleMaxInterval_MapsToFixedInterval()
    {
        var polling = new PgPollingSettings { Enabled = true, IdleMaxInterval = TimeSpan.Zero };
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.True(polling.ApplyTo(builder));
        Assert.Equal(TimeSpan.Zero, builder.Build().IdleMaxInterval);
    }

    [Fact]
    public void Disable_StopsApplying()
    {
        PgPollingSettings polling = new PgPollingSettings().UseProfile().Disable();
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.False(polling.ApplyTo(builder));
        Assert.Equal(OutboxDefaults.Interval, builder.Build().Interval);
    }

    #endregion

    #region Validate

    [Fact]
    public void Validate_IdleBoundBelowInterval_Throws()
    {
        var polling = new PgPollingSettings
        {
            Enabled = true,
            Interval = TimeSpan.FromSeconds(10),
            IdleMaxInterval = TimeSpan.FromSeconds(5),
        };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_NonPositiveInterval_Throws()
    {
        var polling = new PgPollingSettings { Enabled = true, Interval = TimeSpan.Zero };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_NegativeInitialDelay_Throws()
    {
        var polling = new PgPollingSettings { Enabled = true, InitialDelay = TimeSpan.FromSeconds(-1) };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_IterationBudgetBelowMinusOne_Throws()
    {
        var polling = new PgPollingSettings { Enabled = true, MaxProcessingIterations = -2 };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_ZeroTenantParallelism_Throws()
    {
        var polling = new PgPollingSettings { Enabled = true, PerTenantMaxDegreeOfParallelism = 0 };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_TenantParallelismBelowMinusOne_Throws()
    {
        var polling = new PgPollingSettings { Enabled = true, PerTenantMaxDegreeOfParallelism = -2 };

        Assert.Throws<InvalidOperationException>(polling.Validate);
    }

    [Fact]
    public void Validate_DisabledInstance_IsNeverRejected()
    {
        var polling = new PgPollingSettings
        {
            // Garbage that would be rejected when enabled — the layer is off, so it is ignored.
            Interval = TimeSpan.Zero,
            IdleMaxInterval = TimeSpan.FromSeconds(-5),
        };

        polling.Validate(); // does not throw
    }

    #endregion

    #region Provider precedence

    [Fact]
    public void Provider_PollingDefaults_AppliedEvenWithoutConfigurationKey()
    {
        var provider = new PgOutboxDeliveryOptionsProvider(
            configuration: null,
            new PgPollingSettings().UseProfile(PgPollingProfile.Balanced));

        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.True(provider.TryApply("grp", configurationKey: null, builder));

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(TimeSpan.FromSeconds(10), settings.Interval);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.IdleMaxInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.InitialDelay);
    }

    [Fact]
    public void Provider_SectionOverridesPollingDefaults_ForNamedPropertiesOnly()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["grp:Interval"] = "00:00:20",
        });

        var provider = new PgOutboxDeliveryOptionsProvider(
            configuration,
            new PgPollingSettings().UseProfile(PgPollingProfile.Balanced));

        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.True(provider.TryApply("grp", "grp", builder));

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(TimeSpan.FromSeconds(20), settings.Interval);      // section wins
        Assert.Equal(TimeSpan.FromSeconds(30), settings.IdleMaxInterval); // polling default survives
        Assert.Equal(TimeSpan.FromSeconds(5), settings.InitialDelay);
    }

    [Fact]
    public void Provider_UserCallbackWinsOverPollingDefaults()
    {
        var provider = new PgOutboxDeliveryOptionsProvider(
            configuration: null,
            new PgPollingSettings().UseProfile(PgPollingProfile.Balanced));

        // The stage-2 order in Job/Setup: provider first, then the user's configure callback.
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        provider.TryApply("grp", configurationKey: null, builder);
        builder.WithInterval(TimeSpan.FromSeconds(2)).WithFixedInterval();

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(TimeSpan.FromSeconds(2), settings.Interval);
        Assert.Equal(TimeSpan.Zero, settings.IdleMaxInterval);
    }

    [Fact]
    public void Provider_DisabledPolling_IsIgnored()
    {
        var provider = new PgOutboxDeliveryOptionsProvider(
            configuration: null,
            new PgPollingSettings { Enabled = false, Interval = TimeSpan.FromSeconds(3) });

        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.False(provider.TryApply("grp", configurationKey: null, builder));
        Assert.Equal(OutboxDefaults.Interval, builder.Build().Interval);
    }

    [Fact]
    public void Provider_WithoutPollingAndWithoutConfiguration_ReturnsFalse()
    {
        var provider = new PgOutboxDeliveryOptionsProvider(configuration: null);

        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.False(provider.TryApply("grp", configurationKey: null, builder));
        Assert.False(provider.TryApply("grp", "grp", builder));
    }

    #endregion

    #region DI registration

    [Fact]
    public void Di_WithoutOptIn_PollingSettingsAreNotRegistered()
    {
        var services = new ServiceCollection();

        new PgOutboxConfiguration(services).WithOutboxSettings().Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        // No service → the delivery-options provider keeps the pre-existing behaviour.
        Assert.Null(provider.GetService<PgPollingSettings>());
    }

    [Fact]
    public void Di_WithPollingBudget_RegistersValidatedSettings()
    {
        var services = new ServiceCollection();

        new PgOutboxConfiguration(services).WithPollingBudget(TimeSpan.FromMinutes(1)).Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        PgPollingSettings polling = provider.GetRequiredService<PgPollingSettings>();
        Assert.True(polling.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(45), polling.IdleMaxInterval);
    }

    [Fact]
    public void Di_WithPollingProfile_LastCallWins()
    {
        var services = new ServiceCollection();

        new PgOutboxConfiguration(services)
            .WithPollingProfile(PgPollingProfile.Responsive)
            .WithPollingProfile(PgPollingProfile.Quiet)
            .Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        PgPollingSettings polling = provider.GetRequiredService<PgPollingSettings>();
        Assert.Equal(TimeSpan.FromSeconds(30), polling.Interval);
        Assert.Equal(TimeSpan.FromSeconds(45), polling.IdleMaxInterval);
    }

    [Fact]
    public void Di_InconsistentSettings_FailOnResolution()
    {
        var services = new ServiceCollection();

        new PgOutboxConfiguration(services).WithPolling((_, polling) =>
        {
            polling.Enabled = true;
            polling.Interval = TimeSpan.FromSeconds(10);
            polling.IdleMaxInterval = TimeSpan.FromSeconds(5);
        }).Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<PgPollingSettings>());
    }

    [Fact]
    public void Di_WithPolling_ExposesTheInstanceForFurtherTuning()
    {
        var services = new ServiceCollection();

        new PgOutboxConfiguration(services).WithPolling((_, polling) =>
            polling.UseProfile(PgPollingProfile.Balanced)
                   .WithPerTenantParallelism(2)
                   .WithBatchingWindow(TimeSpan.FromSeconds(2)))
            .Commit();

        using ServiceProvider provider = services.BuildServiceProvider();

        PgPollingSettings polling = provider.GetRequiredService<PgPollingSettings>();
        Assert.Equal(2, polling.PerTenantMaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromSeconds(2), polling.BatchingWindow);
    }

    [Fact]
    public void Di_AddSaOutboxUsingPostgreSql_WiresPollingIntoTheDeliveryProvider()
    {
        var services = new ServiceCollection();

        services.AddSaOutboxUsingPostgreSql(cfg => cfg.WithPollingBudget(TimeSpan.FromMinutes(1)));

        using ServiceProvider provider = services.BuildServiceProvider();

        // The end-to-end wiring: Setup.cs must hand PgPollingSettings to the provider, otherwise a
        // group without its own configuration keeps the core defaults.
        IDeliveryGroupOptionsProvider optionsProvider = provider.GetRequiredService<IDeliveryGroupOptionsProvider>();
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        Assert.True(optionsProvider.TryApply("grp", configurationKey: null, builder));
        Assert.Equal(TimeSpan.FromSeconds(45), builder.Build().IdleMaxInterval);
    }

    #endregion
}
