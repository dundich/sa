using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests.Configuration;

/// <summary>
/// End-to-end precedence of a delivery group's settings, through the real two-stage
/// registration: <see cref="OutboxDefaults"/> → the <c>configurationKey</c> section → the user
/// <c>configure</c> callback (final word). The group is resolved from the real consumer manager,
/// so the test exercises the stage-2 upgrade the manager performs on first resolution — no
/// scheduler needed.
/// </summary>
public class DeliveryGroupConfigurationKeyTests(DeliveryGroupConfigurationKeyTests.Fixture fixture)
    : IClassFixture<DeliveryGroupConfigurationKeyTests.Fixture>
{
    private const string GroupId = "prio_cfg";
    private const string ConfigurationKey = "DeliveryGroups:prio_cfg";

    internal sealed class PriorityConsumer : IConsumer<PriorityMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<PriorityMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class NoConfigKeyConsumer : IConsumer<PriorityMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<PriorityMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    internal sealed class PriorityMessage
    {
        public int TenantId { get; set; }
    }

    public sealed class Fixture : PgDataSourceFixture
    {
        private const string OtherGroupKey = "DeliveryGroups:prio_cfg";

        public Fixture() : base()
        {
            // The test's own appsettings section — the fixtures do not register IConfiguration in
            // DI by default, so register it explicitly for the Pg provider to resolve.
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Group "prio_cfg" (keyed) — section values that must land in the settings.
                    [OtherGroupKey + ":MaxBatchSize"] = "64",
                    [OtherGroupKey + ":InitialDelay"] = "00:00:07",
                    // Group "no_config_key" — a section that must be IGNORED: the group was
                    // registered without a configurationKey.
                    ["DeliveryGroups:no_config_key:MaxBatchSize"] = "999",
                })
                .Build();

            Services
                .AddSingleton<IConfiguration>(configuration)
                .AddSaOutbox(builder => builder
                    .WithTenants((_, s) => s.WithTenantIds(1))
                    .WithDeliveries(deliveryBuilder => deliveryBuilder
                        // configurationKey given: section binds between defaults and configure.
                        .AddDelivery<PriorityConsumer, PriorityMessage>(
                            GroupId,
                            (_, b) => b
                                // Code beats the section: the section has no Interval, and even
                                // where it does (none here) the callback has the final word.
                                .WithInterval(TimeSpan.FromMilliseconds(200)),
                            configurationKey: ConfigurationKey)
                        // No configurationKey: the group is shaped by defaults + code only —
                        // the matching section in the configuration must be ignored.
                        .AddDeliveryScoped<NoConfigKeyConsumer, PriorityMessage>(
                            "no_config_key",
                            (_, b) => b
                                .WithInterval(TimeSpan.FromMilliseconds(250)))
                    )
                )
                .AddSaOutboxUsingPostgreSql(cfg =>
                {
                    cfg.WithDataSource(c => c.Options(ob => ob.Configure(o => o.ConnectionString = this.ConnectionString)));
                    cfg.WithOutboxSettings((_, settings) =>
                    {
                        settings.TableSettings.DatabaseSchemaName = "prio_cfg_test";
                        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(1);
                    });
                    cfg.WithMessageSerializer(OutboxMessageSerializer.Instance);
                });
        }
    }

    [Fact]
    public void GroupWithKey_SectionOverridesDefaults_AndConfigureHasFinalWord()
    {
        // Resolving the manager runs its factory: the stage-2 upgrade (section applied) lands
        // here, so Get returns the final composition.
        var manager = fixture.ServiceProvider.GetRequiredService<IOutboxConsumerManager>();

        OutboxConsumerSettings settings = manager.Get(GroupId)
            ?? throw new InvalidOperationException($"Group '{GroupId}' is not registered.");

        // Code (configure) has the final word over the section and the defaults.
        Assert.Equal(TimeSpan.FromMilliseconds(200), settings.Interval);

        // The section overrides the defaults where it names a value…
        Assert.Equal(64, settings.MaxBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(7), settings.InitialDelay);

        // …and leaves everything else at the defaults.
        Assert.Equal(OutboxDefaults.MaxConcurrency, settings.MaxConcurrency);
        Assert.Equal(OutboxDefaults.LookbackInterval, settings.LookbackInterval);
        Assert.Equal(OutboxDefaults.MaxDeliveryAttempts, settings.MaxDeliveryAttempts);
        Assert.Equal(OutboxDefaults.PerTenantMaxDegreeOfParallelism, settings.PerTenantMaxDegreeOfParallelism);

        // AddDelivery registers a singleton consumer — the registration lifetime is not a tunable.
        Assert.True(settings.AsSingleton);
    }

    [Fact]
    public void GroupWithoutKey_IgnoresMatchingSection()
    {
        var manager = fixture.ServiceProvider.GetRequiredService<IOutboxConsumerManager>();

        OutboxConsumerSettings settings = manager.Get("no_config_key")
            ?? throw new InvalidOperationException("Group 'no_config_key' is not registered.");

        // The section named after the group exists in the configuration, but without a
        // configurationKey it must not be consulted.
        Assert.Equal(OutboxDefaults.MaxBatchSize, settings.MaxBatchSize);
        Assert.Equal(OutboxDefaults.InitialDelay, settings.InitialDelay);

        // The configure callback still applies, and AddDeliveryScoped registers a scoped consumer.
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.Interval);
        Assert.False(settings.AsSingleton);
    }

    [Fact]
    public void Stage1Snapshot_PredatesSection_AndStage2UpgradesIt()
    {
        DeliveryGroupSettingsRegistration registration = Assert.Single(
            fixture.ServiceProvider.GetServices<DeliveryGroupSettingsRegistration>(),
            r => r.ConsumerGroupId == GroupId);

        // Stage 1 (registration time): defaults + configure only — no section yet.
        Assert.Equal(OutboxDefaults.MaxBatchSize, registration.RegistrationTimeSettings.MaxBatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(200), registration.RegistrationTimeSettings.Interval);

        // Stage 2 (startup, live provider): the section lands between defaults and configure.
        OutboxConsumerSettings full = registration.BuildFull(fixture.ServiceProvider);
        Assert.Equal(64, full.MaxBatchSize);
        Assert.Equal(TimeSpan.FromSeconds(7), full.InitialDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), full.Interval);
    }
}
