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
/// Verifies the read-only tracking surface against the real storage: the message row, one state per
/// consumer group from the task table, the exception detail from <c>__error$</c> and the attempt
/// timeline from <c>__log$</c>.
/// </summary>
public class PgOutboxMessageTrackerTests(PgOutboxMessageTrackerTests.Fixture fixture)
    : IClassFixture<PgOutboxMessageTrackerTests.Fixture>
{
    private const string Schema = "tracking";
    private const int TenantId = 1;

    private static readonly PermanentException s_permanent = new("boom");

    private sealed class PermanentException(string message) : Exception(message);

    private enum PlanKind
    {
        Ok,
        Retry,
        Error,
    }

    private readonly record struct Plan(PlanKind Kind, TimeSpan Delay)
    {
        public void Apply(IOutboxContextOperations<TestMessage> message)
        {
            switch (Kind)
            {
                case PlanKind.Ok: message.Ok("ok"); break;
                case PlanKind.Retry: message.Retry(Delay, "retry"); break;
                case PlanKind.Error: message.Error501(s_permanent, "permanent"); break;
                default: message.Ok("ok"); break;
            }
        }
    }

    private static class Plans
    {
        private static readonly Dictionary<string, Plan> s_plans = [];

        public static void Set(string payloadId, Plan plan)
        {
            lock (s_plans) s_plans[payloadId] = plan;
        }

        public static bool TryGet(string payloadId, out Plan plan)
        {
            lock (s_plans) return s_plans.TryGetValue(payloadId, out plan);
        }
    }

    private static class Groups
    {
        public const string Ok = "g_ok";
        public const string Error = "g_err";

        public static IReadOnlyList<string> All { get; } = [Ok, Error];
    }

    /// <summary>
    /// The consumer reports what the plan says, or a group-specific default: <see cref="Groups.Ok"/>
    /// succeeds and <see cref="Groups.Error"/> fails. The defaults are what let one test observe two
    /// different states of the same message.
    /// </summary>
    private sealed class TrackingConsumer : IConsumer<TestMessage>
    {
        public ValueTask ConsumeAsync(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            foreach (IOutboxContextOperations<TestMessage> message in messages.Span)
            {
                if (Plans.TryGet(message.Payload.PayloadId, out Plan plan))
                {
                    plan.Apply(message);
                }
                else if (string.Equals(filter.ConsumerGroupId, Groups.Error, StringComparison.Ordinal))
                {
                    message.Error501(s_permanent, "permanent");
                }
                else
                {
                    message.Ok("ok");
                }
            }

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
        private readonly Dictionary<string, OutboxConsumerSettings> _settings = new(StringComparer.Ordinal);

        public Fixture() : base()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, tenants) => tenants.WithTenantIds(TenantId))
                    // TestMessage is not IOutboxPublishable, so without this every row would carry an
                    // empty msg_payload_id and the tracker would have nothing to look up.
                    .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("status", m => m.PayloadId))
                    .WithDeliveries(deliveries =>
                    {
                        foreach (string group in Groups.All)
                        {
                            string captured = group;

                            deliveries.AddDeliveryScoped<TrackingConsumer, TestMessage>(captured, (_, settings) =>
                                _settings[captured] = settings
                                    .WithNoBatchingWindow()
                                    .WithSingleIteration()
                                    .WithMaxBatchSize(32)
                                    .WithMaxDeliveryAttempts(100)
                                    .Build());
                        }
                    }))
                .AddSaOutboxUsingPostgreSql(configuration => configuration
                    .WithDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)))
                    .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
                    .WithOutboxSettings((_, pg) => pg.TableSettings.WithSchema(Schema)));
        }

        public OutboxConsumerSettings SettingsFor(string groupId) => _settings[groupId];
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

    private Task<long> ProcessAsync(string group)
        => fixture.Sub.ProcessMessages<TestMessage>(fixture.SettingsFor(group), Ct);

    /// <summary>
    /// Consumes whatever earlier tests in this class left rentable, so the count one test gets back
    /// belongs to its own message. Messages that are deliberately non-terminal are left alone by the
    /// bounded loop — their payload ids are unique, so they cannot collide with an assertion.
    /// </summary>
    private async Task DrainAsync()
    {
        foreach (string group in Groups.All)
        {
            for (int i = 0; i < 20 && await ProcessAsync(group) > 0; i++)
                await Task.Delay(10, Ct);
        }
    }

    private async Task ForceRentableAsync(string payloadId, string group)
    {
        await fixture.DataSource.ExecuteNonQueryAsync(
            $"""
            UPDATE {Settings.GetQualifiedTaskTableName()}
            SET {Settings.TaskQueue.Fields.TaskLockExpiresOn} = 0
            WHERE {Settings.TaskQueue.Fields.MsgPayloadId} = @payload_id
              AND {Settings.TaskQueue.Fields.ConsumerGroup} = @group
            """,
            [
                new NpgsqlParameter("payload_id", payloadId),
                new NpgsqlParameter("group", group),
            ],
            Ct);
    }

    [Fact]
    public async Task Get_NotLoaded_ReturnsTheStoredMessageWithoutDeliveries()
    {
        string payloadId = PayloadId(nameof(Get_NotLoaded_ReturnsTheStoredMessageWithoutDeliveries));

        await PublishAsync(payloadId);

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Equal(payloadId, trace.PayloadId);
        Assert.Equal(TenantId, trace.TenantId);
        Assert.True(trace.MsgSeq > 0);
        Assert.Empty(trace.Deliveries);
        Assert.False(trace.IsLoaded);
        Assert.Equal(PgOutboxMessageStage.NotLoaded, trace.Stage);
        Assert.Equal(new[] { Groups.Ok, Groups.Error }, trace.GetMissingGroups(Groups.All));

        // The part filter is a real predicate, not decoration: no message lives in this part.
        Assert.Empty(await Tracker.GetAsync(payloadId, TenantId, part: "no_such_part", cancellationToken: Ct));
    }

    [Fact]
    public async Task Get_AfterOk_ReportsTheGroupState()
    {
        string payloadId = PayloadId(nameof(Get_AfterOk_ReportsTheGroupState));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxDeliveryState state = Assert.Single(trace.Deliveries);

        Assert.Equal(Groups.Ok, state.ConsumerGroup);
        Assert.NotNull(state.TaskId);
        Assert.Equal(DeliveryStatusCode.Ok, state.StatusCode);
        Assert.Equal("ok", state.StatusMessage);
        Assert.Equal(1, state.Attempt);
        Assert.NotNull(state.DeliveredAt);
        Assert.True(state.IsTerminal);
        Assert.False(state.IsStuck);
        Assert.Equal(PgOutboxMessageStage.Settled, trace.Stage);
        Assert.Equal(new[] { Groups.Error }, trace.GetMissingGroups(Groups.All));
        Assert.Null(state.ErrorId);
        Assert.Null(state.ErrorType);
        Assert.Null(state.ErrorMessage);

        // The same message is addressable by its persistent id.
        PgOutboxMessageTrace? byId = await Tracker.GetByMessageIdAsync(trace.MessageId, TenantId, cancellationToken: Ct);

        Assert.NotNull(byId);
        Assert.Equal(trace.MessageId, byId.MessageId);
        Assert.Equal(trace.MsgSeq, byId.MsgSeq);
        Assert.Equal(payloadId, byId.PayloadId);
    }

    [Fact]
    public async Task Get_AfterPermanentError_ReturnsTheExceptionDetail()
    {
        string payloadId = PayloadId(nameof(Get_AfterPermanentError_ReturnsTheExceptionDetail));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Error, TimeSpan.Zero));
        await PublishAsync(payloadId);

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxDeliveryState state = Assert.Single(trace.Deliveries);

        Assert.Equal(DeliveryStatusCode.Error501, state.StatusCode);
        Assert.True(state.IsTerminal);
        Assert.Equal(PgOutboxMessageStage.Failed, trace.Stage);
        Assert.NotNull(state.ErrorId);
        Assert.Equal(nameof(PermanentException), state.ErrorType);
        Assert.Contains(nameof(PermanentException), state.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("boom", state.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TwoGroups_ReportsOneStatePerGroup()
    {
        string payloadId = PayloadId(nameof(Get_TwoGroups_ReportsOneStatePerGroup));

        await DrainAsync();
        await PublishAsync(payloadId);

        // No plan: the group-specific defaults decide the outcome.
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));
        Assert.Equal(1L, await ProcessAsync(Groups.Error));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Equal(2, trace.Deliveries.Count);

        PgOutboxDeliveryState ok = trace.Deliveries.Single(d => d.ConsumerGroup == Groups.Ok);
        PgOutboxDeliveryState error = trace.Deliveries.Single(d => d.ConsumerGroup == Groups.Error);

        Assert.Equal(DeliveryStatusCode.Ok, ok.StatusCode);
        Assert.Equal(DeliveryStatusCode.Error501, error.StatusCode);
        Assert.Null(ok.ErrorType);
        Assert.Equal(nameof(PermanentException), error.ErrorType);
    }

    [Fact]
    public async Task GetHistory_ReturnsTheAttemptsInOrder()
    {
        string payloadId = PayloadId(nameof(GetHistory_ReturnsTheAttemptsInOrder));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Retry, TimeSpan.FromSeconds(1)));
        await PublishAsync(payloadId);

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));
        Assert.Equal(DeliveryStatusCode.Retry, (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single().StatusCode);

        // Make the lease expire without waiting for the second-granularity timestamp.
        await ForceRentableAsync(payloadId, Groups.Ok);
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        IReadOnlyList<PgOutboxDeliveryAttempt> history = await Tracker.GetHistoryAsync(
            payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct);

        Assert.Equal(2, history.Count);
        Assert.Equal(DeliveryStatusCode.Retry, history[0].StatusCode);
        Assert.Equal(DeliveryStatusCode.Ok, history[1].StatusCode);
        Assert.True(history[0].CreatedAt <= history[1].CreatedAt);
        Assert.All(history, attempt => Assert.Equal(Groups.Ok, attempt.ConsumerGroup));
        Assert.All(history, attempt => Assert.Null(attempt.ErrorType));

        // The consumer-group filter is optional; without it the same two rows are returned.
        IReadOnlyList<PgOutboxDeliveryAttempt> allGroups =
            await Tracker.GetHistoryAsync(payloadId, TenantId, cancellationToken: Ct);

        Assert.Equal(2, allGroups.Count);
    }

    [Fact]
    public async Task Get_ProcessingWithExpiredLease_IsReportedAsStuck()
    {
        string payloadId = PayloadId(nameof(Get_ProcessingWithExpiredLease_IsReportedAsStuck));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Retry, TimeSpan.FromSeconds(30)));
        await PublishAsync(payloadId);

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        // What a crashed worker leaves behind: still Processing, lease already in the past.
        await fixture.DataSource.ExecuteNonQueryAsync(
            $"""
            UPDATE {Settings.GetQualifiedTaskTableName()}
            SET {Settings.TaskQueue.Fields.DeliveryStatusCode} = {(int)DeliveryStatusCode.Processing},
                {Settings.TaskQueue.Fields.TaskLockExpiresOn} = 0
            WHERE {Settings.TaskQueue.Fields.MsgPayloadId} = @payload_id
              AND {Settings.TaskQueue.Fields.ConsumerGroup} = @group
            """,
            [
                new NpgsqlParameter("payload_id", payloadId),
                new NpgsqlParameter("group", Groups.Ok),
            ],
            Ct);

        PgOutboxDeliveryState state = (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single();

        Assert.Equal(DeliveryStatusCode.Processing, state.StatusCode);
        Assert.True(state.IsStuck);
        Assert.False(state.IsTerminal);
    }


    [Fact]
    public async Task Get_Retry_IsReportedAsInFlight()
    {
        string payloadId = PayloadId(nameof(Get_Retry_IsReportedAsInFlight));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Retry, TimeSpan.FromSeconds(30)));
        await PublishAsync(payloadId);

        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxDeliveryState state = Assert.Single(trace.Deliveries);

        Assert.Equal(DeliveryStatusCode.Retry, state.StatusCode);
        Assert.False(state.IsTerminal);
        Assert.Equal(PgOutboxMessageStage.InFlight, trace.Stage);
    }

    [Fact]
    public async Task GetConsumerGroups_ReturnsTheGroupsThatStarted()
    {
        string payloadId = PayloadId(nameof(GetConsumerGroups_ReturnsTheGroupsThatStarted));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        await ProcessAsync(Groups.Ok);

        IReadOnlyList<PgOutboxConsumerGroup> groups = await Tracker.GetConsumerGroupsAsync(TenantId, Ct);

        PgOutboxConsumerGroup ok = Assert.Single(groups, g => g.ConsumerGroup == Groups.Ok);

        Assert.Equal(TenantId, ok.TenantId);
        Assert.True(ok.OffsetSeq > 0);
        Assert.NotNull(ok.UpdatedAt);
        Assert.All(groups, g => Assert.Equal(TenantId, g.TenantId));
    }

    [Fact]
    public async Task Requeue_SettledMessage_DeliversItAgain()
    {
        string payloadId = PayloadId(nameof(Requeue_SettledMessage_DeliversItAgain));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        Assert.Equal(
            1,
            await Maintenance.RequeueAsync(payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct));

        PgOutboxMessageTrace reset = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxDeliveryState state = Assert.Single(reset.Deliveries);

        Assert.Equal(DeliveryStatusCode.Pending, state.StatusCode);
        Assert.Equal(0, state.Attempt);
        Assert.Null(state.ErrorId);
        Assert.Null(state.DeliveredAt);
        Assert.False(state.IsTerminal);
        Assert.Equal(PgOutboxMessageStage.InFlight, reset.Stage);

        // The reset task is rentable again: the same message is delivered a second time.
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        IReadOnlyList<PgOutboxDeliveryAttempt> history = await Tracker.GetHistoryAsync(
            payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct);

        Assert.Equal(2, history.Count);
        Assert.Equal(DeliveryStatusCode.Ok, history[1].StatusCode);
    }

    [Fact]
    public async Task Requeue_NonTerminalTask_IsLeftAlone()
    {
        string payloadId = PayloadId(nameof(Requeue_NonTerminalTask_IsLeftAlone));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Retry, TimeSpan.FromSeconds(30)));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        // Retry is still owned by the pipeline: resetting it would cancel the scheduled attempt.
        Assert.Equal(
            0,
            await Maintenance.RequeueAsync(payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct));

        PgOutboxDeliveryState state = (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single();

        Assert.Equal(DeliveryStatusCode.Retry, state.StatusCode);
    }

    [Fact]
    public async Task Requeue_WithoutGroup_ResetsEverySettledTask()
    {
        string payloadId = PayloadId(nameof(Requeue_WithoutGroup_ResetsEverySettledTask));

        await DrainAsync();
        await PublishAsync(payloadId);

        // No plan: g_ok succeeds, g_err fails permanently. Both are terminal.
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));
        Assert.Equal(1L, await ProcessAsync(Groups.Error));

        Assert.Equal(2, await Maintenance.RequeueAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Equal(2, trace.Deliveries.Count);
        Assert.All(trace.Deliveries, d => Assert.Equal(DeliveryStatusCode.Pending, d.StatusCode));
        Assert.All(trace.Deliveries, d => Assert.Null(d.ErrorId));
        Assert.Equal(PgOutboxMessageStage.InFlight, trace.Stage);
    }

    [Fact]
    public async Task RequeueByMessageId_ResetsTheTask()
    {
        string payloadId = PayloadId(nameof(RequeueByMessageId_ResetsTheTask));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        Assert.Equal(
            1,
            await Maintenance.RequeueByMessageIdAsync(
                trace.MessageId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct));

        PgOutboxDeliveryState state = (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single();

        Assert.Equal(DeliveryStatusCode.Pending, state.StatusCode);
        Assert.Equal(0, state.Attempt);
    }

    [Fact]
    public void GetMissingGroups_PreservesOrderAndDeduplicates()
    {
        var trace = new PgOutboxMessageTrace
        {
            MessageId = Guid.NewGuid(),
            MsgSeq = 1,
            PayloadId = "payload",
            TenantId = TenantId,
            Part = "part",
            CreatedAt = DateTimeOffset.UtcNow,
            Deliveries =
            [
                new PgOutboxDeliveryState
                {
                    ConsumerGroup = "b",
                    StatusCode = DeliveryStatusCode.Ok,
                    StatusMessage = "ok",
                    TaskCreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };

        Assert.Equal(PgOutboxMessageStage.Settled, trace.Stage);
        Assert.Equal(
            new[] { "a", "c" },
            trace.GetMissingGroups(["a", "b", "a", "c", " "]));
    }

    [Fact]
    public async Task GetMany_ReturnsEveryMatchingMessage()
    {
        string first = PayloadId($"{nameof(GetMany_ReturnsEveryMatchingMessage)}_a");
        string second = PayloadId($"{nameof(GetMany_ReturnsEveryMatchingMessage)}_b");

        await PublishAsync(first);
        await PublishAsync(second);

        IReadOnlyList<PgOutboxMessageTrace> traces = await Tracker.GetManyAsync(
            [first, second, first, " "],
            TenantId,
            cancellationToken: Ct);

        Assert.Equal(2, traces.Count);
        Assert.Equal(new[] { first, second }, traces.Select(t => t.PayloadId));

        Assert.Empty(await Tracker.GetManyAsync(Array.Empty<string>(), TenantId, cancellationToken: Ct));
    }

    [Fact]
    public async Task Count_ReportsStatusesAndStuck()
    {
        string payloadId = PayloadId(nameof(Count_ReportsStatusesAndStuck));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        // Turn the delivered task into a crashed-worker shape: Processing with an expired lease.
        await fixture.DataSource.ExecuteNonQueryAsync(
            $"""
            UPDATE {Settings.GetQualifiedTaskTableName()}
            SET {Settings.TaskQueue.Fields.DeliveryStatusCode} = {(int)DeliveryStatusCode.Processing},
                {Settings.TaskQueue.Fields.TaskLockExpiresOn} = 0
            WHERE {Settings.TaskQueue.Fields.MsgPayloadId} = @payload_id
              AND {Settings.TaskQueue.Fields.ConsumerGroup} = @group
            """,
            [
                new NpgsqlParameter("payload_id", payloadId),
                new NpgsqlParameter("group", Groups.Ok),
            ],
            Ct);

        IReadOnlyList<PgOutboxStatusCount> counts = await Tracker.CountAsync(
            TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct);

        Assert.NotEmpty(counts);
        Assert.All(counts, count => Assert.Equal(Groups.Ok, count.ConsumerGroup));

        PgOutboxStatusCount processing = Assert.Single(
            counts, count => count.StatusCode == DeliveryStatusCode.Processing);

        Assert.True(processing.Count >= 1);
        Assert.True(processing.Stuck >= 1);
    }

    [Fact]
    public async Task GetPayload_ReturnsTheStoredBytesAndType()
    {
        string payloadId = PayloadId(nameof(GetPayload_ReturnsTheStoredBytesAndType));

        await PublishAsync(payloadId);

        PgOutboxMessageTrace trace = Assert.Single(
            await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct));

        PgOutboxMessagePayload? payload = await Tracker.GetPayloadAsync(
            trace.MessageId, TenantId, cancellationToken: Ct);

        Assert.NotNull(payload);
        Assert.Equal(trace.MessageId, payload.MessageId);
        Assert.Equal(nameof(TestMessage), payload.PayloadType);
        Assert.True(payload.PayloadTypeId != 0);
        Assert.True(payload.Size > 0);
        Assert.Equal(payload.Size, payload.Payload.Length);

        Assert.Null(await Tracker.GetPayloadAsync(Guid.NewGuid(), TenantId, cancellationToken: Ct));
    }

    [Fact]
    public async Task RequeueTask_ResetsTheTask()
    {
        string payloadId = PayloadId(nameof(RequeueTask_ResetsTheTask));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        PgOutboxDeliveryState delivered = (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single();

        Assert.NotNull(delivered.TaskId);

        Assert.Equal(
            1,
            await Maintenance.RequeueTaskAsync(delivered.TaskId.Value, TenantId, cancellationToken: Ct));

        PgOutboxDeliveryState reset = (await Tracker.GetAsync(payloadId, TenantId, cancellationToken: Ct))
            .Single().Deliveries.Single();

        Assert.Equal(DeliveryStatusCode.Pending, reset.StatusCode);
        Assert.Equal(0, reset.Attempt);
        Assert.Equal(delivered.TaskId, reset.TaskId);
    }

    [Fact]
    public async Task Search_ReturnsTheTasksOfTheGroupAndStatus()
    {
        string first = PayloadId($"{nameof(Search_ReturnsTheTasksOfTheGroupAndStatus)}_a");
        string second = PayloadId($"{nameof(Search_ReturnsTheTasksOfTheGroupAndStatus)}_b");

        await DrainAsync();
        Plans.Set(first, new Plan(PlanKind.Ok, TimeSpan.Zero));
        Plans.Set(second, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(first);
        await PublishAsync(second);
        await ProcessAsync(Groups.Ok);

        IReadOnlyList<PgOutboxTaskEntry> page = await Tracker.SearchAsync(
            new PgOutboxTaskQuery
            {
                TenantId = TenantId,
                ConsumerGroup = Groups.Ok,
                Statuses = [DeliveryStatusCode.Ok],
                Limit = PgOutboxTaskQuery.MaxLimit,
            },
            Ct);

        Assert.Contains(page, entry => entry.PayloadId == first && entry.StatusCode == DeliveryStatusCode.Ok);
        Assert.Contains(page, entry => entry.PayloadId == second && entry.StatusCode == DeliveryStatusCode.Ok);
        Assert.All(page, entry => Assert.Equal(Groups.Ok, entry.ConsumerGroup));
        Assert.All(page, entry => Assert.Equal(DeliveryStatusCode.Ok, entry.StatusCode));
        Assert.All(page, entry => Assert.True(entry.IsTerminal));
        Assert.All(page, entry => Assert.True(entry.TaskCreatedAt > DateTimeOffset.UnixEpoch));

        // Ordered by message sequence, then task id.
        for (int i = 1; i < page.Count; i++)
        {
            Assert.True(
                page[i - 1].MsgSeq < page[i].MsgSeq
                || (page[i - 1].MsgSeq == page[i].MsgSeq && page[i - 1].TaskId <= page[i].TaskId));
        }
    }

    [Fact]
    public async Task Search_PagesWithTheCursor()
    {
        string payloadId = PayloadId(nameof(Search_PagesWithTheCursor));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        await PublishAsync(payloadId);
        await ProcessAsync(Groups.Ok);

        PgOutboxTaskQuery query = new()
        {
            TenantId = TenantId,
            ConsumerGroup = Groups.Ok,
            Limit = 1,
        };

        PgOutboxTaskEntry? previous = null;
        int seen = 0;

        while (seen < 5)
        {
            IReadOnlyList<PgOutboxTaskEntry> page = await Tracker.SearchAsync(
                previous is null ? query : query with { After = previous.Cursor },
                Ct);

            if (page.Count == 0) break;

            PgOutboxTaskEntry entry = Assert.Single(page);

            if (previous is not null)
            {
                Assert.True(
                    entry.MsgSeq > previous.MsgSeq
                    || (entry.MsgSeq == previous.MsgSeq && entry.TaskId > previous.TaskId));
            }

            previous = entry;
            seen++;
        }

        Assert.True(seen > 0, "The queue must contain at least the message this test published.");
    }

    [Fact]
    public async Task Search_EmptyStatuses_MatchesNothing()
    {
        await DrainAsync();

        Assert.Empty(await Tracker.SearchAsync(
            new PgOutboxTaskQuery
            {
                TenantId = TenantId,
                Statuses = [],
            },
            Ct));
    }

    [Fact]
    public async Task Get_AnotherTenant_ReturnsNothing()
    {
        string payloadId = PayloadId(nameof(Get_AnotherTenant_ReturnsNothing));

        await PublishAsync(payloadId);

        // The lookup is tenant-scoped by construction: the message table is list-partitioned by
        // (tenant, part), so another tenant's leaves cannot contain this row.
        Assert.Empty(await Tracker.GetAsync(payloadId, TenantId + 100, cancellationToken: Ct));
        Assert.Empty(await Tracker.GetHistoryAsync(payloadId, TenantId + 100, cancellationToken: Ct));
    }

    [Fact]
    public async Task GetHistory_Limit_KeepsTheNewestAttempts()
    {
        string payloadId = PayloadId(nameof(GetHistory_Limit_KeepsTheNewestAttempts));

        await DrainAsync();
        Plans.Set(payloadId, new Plan(PlanKind.Retry, TimeSpan.FromSeconds(1)));
        await PublishAsync(payloadId);
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        await ForceRentableAsync(payloadId, Groups.Ok);
        Plans.Set(payloadId, new Plan(PlanKind.Ok, TimeSpan.Zero));
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        Assert.Equal(
            1,
            await Maintenance.RequeueAsync(payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct));
        Assert.Equal(1L, await ProcessAsync(Groups.Ok));

        IReadOnlyList<PgOutboxDeliveryAttempt> all = await Tracker.GetHistoryAsync(
            payloadId, TenantId, consumerGroup: Groups.Ok, cancellationToken: Ct);

        Assert.Equal(3, all.Count);

        IReadOnlyList<PgOutboxDeliveryAttempt> newest = await Tracker.GetHistoryAsync(
            payloadId, TenantId, consumerGroup: Groups.Ok, limit: 2, cancellationToken: Ct);

        Assert.Equal(2, newest.Count);
        Assert.Equal(all[^2].DeliveryId, newest[0].DeliveryId);
        Assert.Equal(all[^1].DeliveryId, newest[1].DeliveryId);
    }

    [Fact]
    public async Task InvalidArguments_AreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Tracker.GetAsync(" ", TenantId, cancellationToken: Ct).AsTask());

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Tracker.GetAsync(
                "payload",
                TenantId,
                fromDate: now,
                toDate: now.AddDays(-1),
                cancellationToken: Ct).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Tracker.GetHistoryAsync("payload", TenantId, limit: 0, cancellationToken: Ct).AsTask());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Tracker.SearchAsync(
                new PgOutboxTaskQuery { TenantId = TenantId, Limit = 0 },
                Ct).AsTask());
    }
}
