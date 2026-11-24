using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Tracking;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Tracking;

/// <summary>
/// Verifies the "which tables exist" probe against a database that is genuinely incomplete: a fresh
/// deployment has no task or delivery table until the first load / first delivery, and a table the
/// probe has already confirmed can still be dropped behind its back.
/// </summary>
/// <remarks>
/// The class owns a database of its own (<see cref="IClassFixture{TFixture}"/>) because these tests
/// drop tables, which would poison any class that expects the full schema. Both tests leave the task
/// and delivery tables absent, so they do not depend on the order xUnit runs them in.
/// </remarks>
public class PgOutboxStorageTablesProbeTests(PgOutboxStorageTablesProbeTests.Fixture fixture)
    : IClassFixture<PgOutboxStorageTablesProbeTests.Fixture>
{
    private const string Schema = "probe";
    private const int TenantId = 1;
    private const string Group = "g_probe";

    private sealed class ProbeConsumer : IConsumer<TestMessage>
    {
        public ValueTask ConsumeAsync(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            foreach (IOutboxContextOperations<TestMessage> message in messages.Span)
                message.Ok("ok");

            return ValueTask.CompletedTask;
        }
    }

    /// <remarks>
    /// Derives from <see cref="PgDataSourceFixture{TSub}"/> rather than the outbox helper: the latter
    /// already registers the PostgreSQL provider in its constructor, and registering it twice rebuilds
    /// the partitioned tables with a duplicated partition key.
    /// </remarks>
    public class Fixture : PgDataSourceFixture<IDeliveryProcessor>
    {
        private OutboxConsumerSettings? _settings;

        public Fixture() : base()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, tenants) => tenants.WithTenantIds(TenantId))
                    // TestMessage is not IOutboxPublishable, so without this every row would carry an
                    // empty msg_payload_id and the tracker would have nothing to look up.
                    .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("status", m => m.PayloadId))
                    .WithDeliveries(deliveries => deliveries
                        .AddDeliveryScoped<ProbeConsumer, TestMessage>(Group, (_, settings) =>
                            _settings = settings
                                .WithNoBatchingWindow()
                                .WithSingleIteration()
                                .WithMaxBatchSize(32)
                                .Build())))
                .AddSaOutboxUsingPostgreSql(configuration => configuration
                    .WithDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)))
                    .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
                    .WithOutboxSettings((_, pg) => pg.TableSettings.WithSchema(Schema)));
        }

        public OutboxConsumerSettings ConsumerSettings => _settings
            ?? throw new InvalidOperationException("The delivery module has not been configured yet.");
    }

    private IPgOutboxMessageTracker Tracker =>
        fixture.ServiceProvider.GetRequiredService<IPgOutboxMessageTracker>();

    private IPgOutboxMessageMaintenance Maintenance =>
        fixture.ServiceProvider.GetRequiredService<IPgOutboxMessageMaintenance>();

    private PgOutboxTableSettings Settings =>
        fixture.ServiceProvider.GetRequiredService<PgOutboxTableSettings>();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PayloadId(string test) => $"{test}_{Guid.NewGuid():N}";

    private async Task PublishAsync(string payloadId)
    {
        ulong published = await fixture.ServiceProvider
            .GetRequiredService<IOutboxMessagePublisher>()
            .Publish(
                [new TestMessage { PayloadId = payloadId, Content = payloadId, TenantId = TenantId }],
                m => m.TenantId,
                Ct);

        Assert.True(published > 0);
    }

    private Task<long> ProcessAsync()
        => fixture.Sub.ProcessMessages<TestMessage>(fixture.ConsumerSettings, Ct);

    private Task DropAsync(string qualifiedName)
        => fixture.DataSource.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {qualifiedName} CASCADE;", Ct);

    [Fact]
    public async Task Reads_OnAFreshDeployment_ReportTheMessageWithoutDeliveries()
    {
        // The task, delivery, error and type tables are created lazily. A read against a database that
        // has only ever been published to must resolve the "no task table" variants and answer with
        // the message alone — not throw 42P01, and not report a state it cannot know.
        string payloadId = PayloadId(nameof(Reads_OnAFreshDeployment_ReportTheMessageWithoutDeliveries));

        await PublishAsync(payloadId);

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Equal(payloadId, trace.PayloadId);
        Assert.Empty(trace.Deliveries);
        Assert.False(trace.IsLoaded);
        Assert.Equal(new[] { Group }, trace.GetMissingGroups([Group]));

        Assert.NotNull(await Tracker.GetByMessageIdAsync(trace.MessageId, TenantId, cancellationToken: Ct));

        // The payload reader needs the type table, which this database does not have either.
        PgOutboxMessagePayload? payload = await Tracker.GetPayloadAsync(trace.MessageId, TenantId, cancellationToken: Ct);
        Assert.NotNull(payload);
        Assert.Equal(trace.MessageId, payload.MessageId);

        // The half of the surface that queries the missing tables directly.
        Assert.Empty(await Tracker.CountAsync(TenantId, cancellationToken: Ct));
        Assert.Empty(await Tracker.SearchAsync(new PgOutboxTaskQuery { TenantId = TenantId }, Ct));
        Assert.Empty(await Tracker.GetHistoryAsync(payloadId, TenantId, cancellationToken: Ct));
        Assert.Equal(0, await Maintenance.RequeueAsync(payloadId, TenantId, cancellationToken: Ct));
    }

    [Fact]
    public async Task Reads_AfterAConfirmedTableIsDropped_ReResolveInsteadOfThrowing()
    {
        // The probe remembers "exists" for the life of the process. Drop the task and delivery tables
        // once they have been confirmed: the statement fails with 42P01, the probe forgets the answer
        // and the read runs again against the corrected set instead of surfacing the error.
        string payloadId = PayloadId(nameof(Reads_AfterAConfirmedTableIsDropped_ReResolveInsteadOfThrowing));

        await PublishAsync(payloadId);

        // Whatever earlier tests in this class left pending is delivered in the same pass.
        Assert.True(await ProcessAsync() > 0);

        PgOutboxMessageTrace loaded = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Single(loaded.Deliveries);
        Assert.True(loaded.IsLoaded);

        await DropAsync(Settings.GetQualifiedTaskTableName());
        await DropAsync(Settings.GetQualifiedDeliveryTableName());

        // Every call below would throw 42P01 without the retry, because the probe had both tables
        // remembered as existing.
        PgOutboxMessageTrace dropped = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Empty(dropped.Deliveries);
        Assert.False(dropped.IsLoaded);

        Assert.Empty(await Tracker.CountAsync(TenantId, cancellationToken: Ct));
        Assert.Empty(await Tracker.SearchAsync(new PgOutboxTaskQuery { TenantId = TenantId }, Ct));
        Assert.Empty(await Tracker.GetHistoryAsync(payloadId, TenantId, cancellationToken: Ct));
        Assert.Equal(0, await Maintenance.RequeueAsync(payloadId, TenantId, cancellationToken: Ct));
    }
}
