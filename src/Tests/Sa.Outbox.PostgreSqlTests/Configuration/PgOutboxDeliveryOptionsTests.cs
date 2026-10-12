using Microsoft.Extensions.Configuration;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// Unit tests of <see cref="PgOutboxDeliveryOptions"/> / <see cref="PgOutboxDeliveryOptionsProvider"/>:
/// section binding, the non-null-only <c>ApplyTo</c> contract and the no-op paths (absent
/// <c>IConfiguration</c>, empty key, missing/empty section).
/// </summary>
public class PgOutboxDeliveryOptionsTests
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    [Fact]
    public void FromConfiguration_BindsAllProperties()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["DeliveryGroups:grp_a:Interval"] = "00:00:01",
            ["DeliveryGroups:grp_a:InitialDelay"] = "00:00:02",
            ["DeliveryGroups:grp_a:ConcurrencyLimit"] = "2",
            ["DeliveryGroups:grp_a:MaxConcurrency"] = "3",
            ["DeliveryGroups:grp_a:RetryCountOnError"] = "4",
            ["DeliveryGroups:grp_a:MaxBatchSize"] = "5",
            ["DeliveryGroups:grp_a:MaxProcessingIterations"] = "6",
            ["DeliveryGroups:grp_a:IterationDelay"] = "00:00:07",
            ["DeliveryGroups:grp_a:LockDuration"] = "00:00:08",
            ["DeliveryGroups:grp_a:LockRenewal"] = "00:00:09",
            ["DeliveryGroups:grp_a:LookbackInterval"] = "00:01:00",
            ["DeliveryGroups:grp_a:MaxDeliveryAttempts"] = "11",
            ["DeliveryGroups:grp_a:BatchingWindow"] = "00:00:12",
            ["DeliveryGroups:grp_a:PerTenantTimeout"] = "00:00:13",
            ["DeliveryGroups:grp_a:PerTenantMaxDegreeOfParallelism"] = "14",
            ["DeliveryGroups:grp_a:IdleMaxInterval"] = "00:00:15",
        });

        PgOutboxDeliveryOptions? options = PgOutboxDeliveryOptions.FromConfiguration(configuration, "DeliveryGroups:grp_a");

        Assert.NotNull(options);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.InitialDelay);
        Assert.Equal(2, options.ConcurrencyLimit);
        Assert.Equal(3, options.MaxConcurrency);
        Assert.Equal(4, options.RetryCountOnError);
        Assert.Equal(5, options.MaxBatchSize);
        Assert.Equal(6, options.MaxProcessingIterations);
        Assert.Equal(TimeSpan.FromSeconds(7), options.IterationDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), options.LockDuration);
        Assert.Equal(TimeSpan.FromSeconds(9), options.LockRenewal);
        Assert.Equal(TimeSpan.FromMinutes(1), options.LookbackInterval);
        Assert.Equal(11, options.MaxDeliveryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(12), options.BatchingWindow);
        Assert.Equal(TimeSpan.FromSeconds(13), options.PerTenantTimeout);
        Assert.Equal(14, options.PerTenantMaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromSeconds(15), options.IdleMaxInterval);
    }

    [Fact]
    public void FromConfiguration_PartialSection_LeavesUnspecifiedPropertiesNull()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["grp:Interval"] = "00:00:01",
        });

        PgOutboxDeliveryOptions? options = PgOutboxDeliveryOptions.FromConfiguration(configuration, "grp");

        Assert.NotNull(options);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Interval);
        // Everything not named stays null — "not specified", not "reset to zero".
        Assert.Null(options.InitialDelay);
        Assert.Null(options.ConcurrencyLimit);
        Assert.Null(options.MaxConcurrency);
        Assert.Null(options.RetryCountOnError);
        Assert.Null(options.MaxBatchSize);
        Assert.Null(options.MaxProcessingIterations);
        Assert.Null(options.IterationDelay);
        Assert.Null(options.LockDuration);
        Assert.Null(options.LockRenewal);
        Assert.Null(options.LookbackInterval);
        Assert.Null(options.MaxDeliveryAttempts);
        Assert.Null(options.BatchingWindow);
        Assert.Null(options.PerTenantTimeout);
        Assert.Null(options.PerTenantMaxDegreeOfParallelism);
        Assert.Null(options.IdleMaxInterval);
    }

    [Fact]
    public void FromConfiguration_NullKey_ReturnsNull()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["grp:Interval"] = "00:00:01",
        });

        Assert.Null(PgOutboxDeliveryOptions.FromConfiguration(configuration, null!));
        Assert.Null(PgOutboxDeliveryOptions.FromConfiguration(configuration, string.Empty));
        Assert.Null(PgOutboxDeliveryOptions.FromConfiguration(configuration, "   "));
    }

    [Fact]
    public void FromConfiguration_MissingSection_ReturnsNull()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["other:Interval"] = "00:00:01",
        });

        Assert.Null(PgOutboxDeliveryOptions.FromConfiguration(configuration, "grp"));
    }

    [Fact]
    public void FromConfiguration_PresentButEmptySection_BindsEverythingNull()
    {
        // A present-but-empty section binds every property to null — indistinguishable from
        // "apply nothing".
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["grp:"] = string.Empty,
        });

        PgOutboxDeliveryOptions? options = PgOutboxDeliveryOptions.FromConfiguration(configuration, "grp");

        Assert.NotNull(options);
        Assert.False(options.ApplyTo(new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp")));
    }

    [Fact]
    public void FromConfiguration_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PgOutboxDeliveryOptions.FromConfiguration(null!, "grp"));
    }

    [Fact]
    public void ApplyTo_PartialOptions_OverridesOnlyNamedValues()
    {
        // The builder carries values the section does not name (e.g. set by OutboxDefaults or an
        // earlier layer) — those must survive ApplyTo untouched.
        OutboxConsumerSettingsBuilder builder = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("grp")
            .WithInterval(TimeSpan.FromSeconds(1))
            .WithMaxBatchSize(50)
            .WithLookbackInterval(TimeSpan.FromDays(2));

        PgOutboxDeliveryOptions options = new()
        {
            Interval = TimeSpan.FromSeconds(2),
            MaxBatchSize = 200,
            // MaxConcurrency deliberately null — the builder's default must be preserved.
        };

        bool applied = options.ApplyTo(builder);

        Assert.True(applied);

        OutboxConsumerSettings settings = builder.Build();
        // Named values are overridden…
        Assert.Equal(TimeSpan.FromSeconds(2), settings.Interval);
        Assert.Equal(200, settings.MaxBatchSize);
        // …unspecified values keep the builder's previous state (defaults).
        Assert.Equal(OutboxDefaults.MaxConcurrency, settings.MaxConcurrency);
        Assert.Equal(TimeSpan.FromDays(2), settings.LookbackInterval);
    }

    [Fact]
    public void ApplyTo_AllNull_ReturnsFalse()
    {
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        bool applied = new PgOutboxDeliveryOptions().ApplyTo(builder);

        Assert.False(applied);
    }

    [Fact]
    public void ApplyTo_IdleMaxIntervalZero_MapsToFixedInterval()
    {
        // Zero disables the adaptive back-off — it must not be fed to WithIdleBackoff, which
        // rejects non-positive values.
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        bool applied = new PgOutboxDeliveryOptions { IdleMaxInterval = TimeSpan.Zero }.ApplyTo(builder);

        Assert.True(applied);
        Assert.Equal(TimeSpan.Zero, builder.Build().IdleMaxInterval);
    }

    [Fact]
    public void ApplyTo_PositiveIdleMaxInterval_EnablesBackoff()
    {
        var builder = new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp");

        bool applied = new PgOutboxDeliveryOptions { IdleMaxInterval = TimeSpan.FromSeconds(30) }.ApplyTo(builder);

        Assert.True(applied);
        Assert.Equal(TimeSpan.FromSeconds(30), builder.Build().IdleMaxInterval);
    }

    #region Provider

    [Fact]
    public void Provider_NoIConfiguration_ReturnsFalse()
    {
        PgOutboxDeliveryOptionsProvider provider = new(configuration: null);

        Assert.False(provider.TryApply("grp", "grp", new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp")));
    }

    [Fact]
    public void Provider_EmptyKey_ReturnsFalse_EvenWhenSectionExists()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["grp:Interval"] = "00:00:01",
        });
        PgOutboxDeliveryOptionsProvider provider = new(configuration);

        Assert.False(provider.TryApply("grp", string.Empty, new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp")));
        Assert.False(provider.TryApply("grp", "   ", new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp")));
    }

    [Fact]
    public void Provider_MissingSection_ReturnsFalse()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["other:Interval"] = "00:00:01",
        });
        PgOutboxDeliveryOptionsProvider provider = new(configuration);

        Assert.False(provider.TryApply("grp", "grp", new OutboxConsumerSettingsBuilder().WithConsumerGroupId("grp")));
    }

    [Fact]
    public void Provider_BindsSectionAndAppliesNonNullValues()
    {
        IConfiguration configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["DeliveryGroups:grp_a:MaxBatchSize"] = "128",
            ["DeliveryGroups:grp_a:IdleMaxInterval"] = "00:00:00",
        });
        PgOutboxDeliveryOptionsProvider provider = new(configuration);

        var builder = new OutboxConsumerSettingsBuilder()
            .WithConsumerGroupId("grp_a")
            .WithMaxBatchSize(50);

        bool applied = provider.TryApply("grp_a", "DeliveryGroups:grp_a", builder);

        Assert.True(applied);

        OutboxConsumerSettings settings = builder.Build();
        Assert.Equal(128, settings.MaxBatchSize);
        Assert.Equal(TimeSpan.Zero, settings.IdleMaxInterval); // zero → WithFixedInterval()
    }

    #endregion
}
