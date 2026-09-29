using Microsoft.Extensions.DependencyInjection;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// Verifies that consumer-set statuses produce the expected *database* effects, not just the
/// courier's in-memory counting: the persisted status code, <c>delivery_attempt</c>,
/// <c>task_lock_expires_on</c>, <c>error_id</c> and the presence of an <c>__error$</c> row.
/// </summary>
public class DeliveryConsumerStatusTests(DeliveryConsumerStatusTests.Fixture fixture)
    : IClassFixture<DeliveryConsumerStatusTests.Fixture>
{
    private const string Schema = "consumer_status";

    // Distinct exception *types* on purpose: __error$ rows are looked up by type name, and two
    // InvalidOperationExceptions would make the transient-Warn assertions see the permanent
    // error row written by another test. Shared instances keep de-duplication deterministic.
    private static readonly WarnException s_warn = new();
    private static readonly PermanentException s_error = new();

    private sealed class WarnException : Exception
    {
        public const string TypeName = nameof(WarnException);
        public override string ToString() => TypeName;
    }

    private sealed class PermanentException : Exception
    {
        public const string TypeName = nameof(PermanentException);
        public override string ToString() => TypeName;
    }

    public enum Action
    {
        /// <summary>Leave the context completely untouched — no status call at all.</summary>
        Untouched,
        Ok,
        Created,
        Accepted,
        NoContent,
        Aborted,
        MovedPermanently,
        Postpone,
        Retry,
        Warn,
        Error501,
    }

    /// <summary>What the consumer should do with a given payload id.</summary>
    internal readonly record struct Plan(Action Action, TimeSpan Delay)
    {
        public void Apply(IOutboxContextOperations<TestMessage> msg)
        {
            switch (Action)
            {
                case Action.Untouched: break;
                case Action.Ok: msg.Ok("ok"); break;
                case Action.Created: msg.Created("created"); break;
                case Action.Accepted: msg.Accepted("accepted"); break;
                case Action.NoContent: msg.NoContent("no content"); break;
                case Action.Aborted: msg.Aborted("aborted"); break;
                case Action.MovedPermanently: msg.MovedPermanently("moved"); break;

                // NB: Postpone is documented as deliberately NOT consuming an attempt.
                case Action.Postpone: msg.Postpone(Delay, "postponed"); break;
                case Action.Retry: msg.Retry(Delay, "retry"); break;

                case Action.Warn: msg.Warn(s_warn, "warned", Delay); break;
                case Action.Error501: msg.Error501(s_error, "permanent"); break;
            }
        }
    }

    internal static class Plans
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

    class StatusConsumer : IConsumer<TestMessage>
    {
        public async ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken);

            // NB: IOutboxContext.PayloadId is the outbox-generated payload id, NOT
            // TestMessage.PayloadId. The plan has to be keyed off the deserialized payload.
            foreach (var msg in messages.Span)
            {
                if (Plans.TryGet(msg.Payload.PayloadId, out var plan))
                    plan.Apply(msg);
                else
                    msg.Ok("default");
            }
        }
    }

    /// <summary>
    /// One consumer group per test method. Groups are processed independently, so the number
    /// returned by <c>ProcessMessages</c> only ever refers to the messages the calling test
    /// published — a shared group would make that count depend on test execution order.
    /// </summary>
    public static class Groups
    {
        public const string SuccessStatus = "g_success";
        public const string PostponeKeepsTask = "g_postpone_keeps";
        public const string PostponeRerented = "g_postpone_rerented";
        public const string Retry = "g_retry";
        public const string Warn = "g_warn";
        public const string Error5xx = "g_error5xx";
        public const string MovedPermanently = "g_moved";
        public const string Untouched = "g_untouched";

        // NB: declaration order matters — static initializers run top to bottom, so the two lists
        // All spreads must be declared before it.
        private static IReadOnlyList<string> Base { get; } =
        [
            PostponeKeepsTask, PostponeRerented, Retry,
            Warn, Error5xx, MovedPermanently, Untouched
        ];

        /// <summary>
        /// One group per row of the 2xx theory. A single shared group would work only because every
        /// 2xx status is terminal and therefore never re-rentable — the assertion that a second
        /// cycle returns 0 would then be trivially true for the wrong reason, and any future row
        /// that is not terminal would silently make the handled count depend on execution order.
        /// </summary>
        private static IReadOnlyList<string> SuccessStatusCases { get; } =
            [.. Enum.GetValues<Action>()
                .Where(a => a is Action.Ok or Action.Created or Action.Accepted or Action.NoContent or Action.Aborted)
                .Select(a => $"{SuccessStatus}_{a}")];

        public static IReadOnlyList<string> All { get; } =
        [
            .. Base,
            .. SuccessStatusCases
        ];
    }

    /// <remarks>
    /// Derives from <see cref="PgDataSourceFixture{T}"/> rather than
    /// <c>OutboxPostgreSqlFixture&lt;T&gt;</c> on purpose: the latter already calls
    /// <c>AddSaOutboxUsingPostgreSql</c> in its base constructor, and a second call re-registers the
    /// partitioned tables, producing a root DDL with a duplicated <c>tenant_id</c> in the primary key
    /// (PostgreSQL 42701). Registering once, with a dedicated schema, also isolates this class's
    /// rows from every other fixture in the assembly.
    /// </remarks>
    public class Fixture : PgDataSourceFixture<IDeliveryProcessor>
    {
        private readonly Dictionary<string, OutboxConsumerSettings> _settings = new(StringComparer.Ordinal);

        public Fixture() : base()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, s) => s.WithTenantIds(1))
                    // TestMessage does not implement IOutboxPublishable, so without this the outbox
                    // writes msg_payload_id = '' for every row and the tests have no way to tell
                    // their own tasks apart. Mapping TestMessage.PayloadId through makes
                    // msg_payload_id (and IOutboxContext.PayloadId) the id used below.
                    .WithMetadata((_, b) => b.AddMetadata<TestMessage>("status", m => m.PayloadId))
                    .WithDeliveries(b =>
                    {
                        foreach (string group in Groups.All)
                        {
                            string captured = group;

                            b.AddDeliveryScoped<StatusConsumer, TestMessage>(captured, (_, s) =>
                                _settings[captured] = s
                                    .WithNoBatchingWindow()
                                    .WithNoLockDuration()
                                    .WithLockRenewal(TimeSpan.FromMilliseconds(10))
                                    .WithMaxDeliveryAttempts(100)
                                    .WithMaxBatchSize(32)
                                    .WithSingleIteration()
                                    .Build());
                        }
                    })
                )
                .AddSaOutboxUsingPostgreSql(builder => builder
                    .WithDataSource(b => b.WithConnectionString(_ => ConnectionString))
                    .WithMessageSerializer(_ => OutboxMessageSerializer.Instance)
                    .WithOutboxSettings((_, pg) => pg.TableSettings.WithSchema(Schema))
                );
        }

        public OutboxConsumerSettings SettingsFor(string groupId) => _settings[groupId];

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }

    private IDeliveryProcessor Sub => fixture.Sub;

    private PgOutboxTableSettings TableSettings =>
        fixture.ServiceProvider.GetRequiredService<PgOutboxTableSettings>();

    // The schema lives on PgOutboxTableSettings itself, so every table is qualified by hand.
    private string TaskQueue => Qual(TableSettings.TaskQueue.TableName);
    private string DeliveryTable => Qual(TableSettings.Delivery.TableName);
    private string ErrorTable => Qual(TableSettings.Error.TableName);
    private string Qual(string tableName) => $"{TableSettings.DatabaseSchemaName}.{tableName}";

    private CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PayloadId(string test, string suffix) => $"{test}_{suffix}";

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task PublishAsync(string payloadId)
    {
        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = payloadId, Content = payloadId, TenantId = 1 }
        ];

        var published = await fixture.Publisher.Publish(messages, m => m.TenantId, Token);
        Assert.True(published > 0);
    }

    private Task<DeliveryStatusCode> TaskStatusAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirst<DeliveryStatusCode>(
            $"""
            SELECT {TableSettings.TaskQueue.Fields.DeliveryStatusCode}
            FROM {TaskQueue}
            WHERE {TableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, Token);

    private Task<int> TaskAttemptAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirst<int>(
            $"""
            SELECT {TableSettings.TaskQueue.Fields.DeliveryAttempt}
            FROM {TaskQueue}
            WHERE {TableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, Token);

    // COALESCE because the data source has no Nullable<T> reader support, and 0 unambiguously
    // means "no error id" (ids are sequence-generated and start at 1).
    private Task<long> TaskErrorIdAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirst<long>(
            $"""
            SELECT COALESCE({TableSettings.TaskQueue.Fields.ErrorId}, 0)
            FROM {TaskQueue}
            WHERE {TableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, Token);

    private Task<long> TaskLockExpiresOnAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirst<long>(
            $"""
            SELECT {TableSettings.TaskQueue.Fields.TaskLockExpiresOn}
            FROM {TaskQueue}
            WHERE {TableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, Token);

    private Task<int> DeliveryRowCountAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirst<int>(
            $"""
            SELECT COUNT({TableSettings.Delivery.Fields.DeliveryId})
            FROM {DeliveryTable}
            WHERE {TableSettings.Delivery.Fields.MsgPayloadId} = '{payloadId}'
            """, Token);

    /// <summary>
    /// The <c>__error$</c> table is created lazily, on the first error write, so it may not exist
    /// yet. Querying it unconditionally would fail with 42P01 and mask the actual assertion.
    /// </summary>
    private async Task<int> ErrorRowsForAsync(string exceptionType)
    {
        if (!await ErrorTableExistsAsync())
            return 0;

        return await fixture.DataSource.ExecuteReaderFirst<int>(
            $"""
            SELECT COUNT({TableSettings.Error.Fields.ErrorId})
            FROM {ErrorTable}
            WHERE {TableSettings.Error.Fields.ErrorType} = '{exceptionType}'
            """, Token);
    }

    /// <summary>
    /// Total number of rows in <c>__error$</c>, regardless of type.
    /// </summary>
    /// <remarks>
    /// The table has no payload id, so a row can only be attributed to a test by its exception type —
    /// which is ambiguous as soon as two tests write the same type. For "this delivery wrote nothing"
    /// assertions, compare the total against a snapshot taken before the delivery instead. That is
    /// immune to test ordering.
    /// </remarks>
    private async Task<int> TotalErrorRowsAsync()
    {
        if (!await ErrorTableExistsAsync())
            return 0;

        return await fixture.DataSource.ExecuteReaderFirst<int>(
            $"SELECT COUNT({TableSettings.Error.Fields.ErrorId}) FROM {ErrorTable}", Token);
    }

    private async Task<bool> ErrorTableExistsAsync()
        => await fixture.DataSource.ExecuteReaderFirst<int>(
            $"SELECT COUNT(*) FROM pg_class WHERE relname = '{TableSettings.Error.TableName}'",
            Token) > 0;

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Statuses a task can never be rented again in, so the courier's handled count and
    /// <c>ShouldContinueProcessing</c> both ignore them.
    /// </summary>
    private static readonly DeliveryStatusCode[] s_terminal =
    [
        DeliveryStatusCode.Ok, DeliveryStatusCode.Created, DeliveryStatusCode.Accepted,
        DeliveryStatusCode.Ok203, DeliveryStatusCode.NoContent, DeliveryStatusCode.Aborted,
        DeliveryStatusCode.MovedPermanently,
        DeliveryStatusCode.Error, DeliveryStatusCode.Error501, DeliveryStatusCode.Error502,
        DeliveryStatusCode.Error503, DeliveryStatusCode.Error504, DeliveryStatusCode.Error505,
        DeliveryStatusCode.Error506, DeliveryStatusCode.Error507,
        DeliveryStatusCode.MaximumAttemptsError
    ];

    /// <summary>
    /// Marks every still-rentable task terminal, so the leftovers the bounded drain could not finish
    /// cannot be counted by a later test.
    /// </summary>
    /// <remarks>
    /// The drain above cannot converge by design: a message left in Postpone, Retry or Warn stays
    /// rent-eligible, so <c>ProcessMessages</c> keeps returning non-zero and the loop gives up at its
    /// bound with the task still pending. Those leftovers sit in the same consumer groups the calling
    /// test uses, so as soon as their delay elapses they are delivered again and counted together
    /// with the test's own message — the handled count stops being specific to the test, and which
    /// case fails depends on execution order.
    /// </remarks>
    private async Task ResetRentableTasksAsync()
    {
        // The task queue is created lazily on the first delivery, so before the first test of the
        // class it does not exist yet — and there is nothing to neutralise in that case anyway.
        if (!await TaskQueueExistsAsync())
            return;

        string terminal = string.Join(",", s_terminal.Select(c => ((int)c).ToString()));

        await fixture.DataSource.ExecuteNonQuery(
            $"""
            UPDATE {TaskQueue}
            SET {TableSettings.TaskQueue.Fields.DeliveryStatusCode} = {(int)DeliveryStatusCode.Ok}
            WHERE {TableSettings.TaskQueue.Fields.DeliveryStatusCode} NOT IN ({terminal})
            """, Token);
    }

    private async Task<bool> TaskQueueExistsAsync()
        => await fixture.DataSource.ExecuteReaderFirst<int>(
            $"SELECT COUNT(*) FROM pg_class WHERE relname = '{TableSettings.TaskQueue.TableName}'",
            Token) > 0;

    /// <summary>
    /// Publishing a message creates a task row for <em>every</em> registered consumer group of that
    /// message type, so each test leaves pending work behind in the other groups. Draining all
    /// groups first makes the count returned by <c>ProcessMessages</c> specific to the calling test,
    /// and — more importantly — stops a later test from re-delivering an earlier test's message and
    /// appending a second row to the shared <c>__error$</c> table. As a bonus this is what creates
    /// the task queue tables, which the outbox only builds lazily on the first delivery.
    /// </summary>
    private async Task DrainAsync()
    {
        foreach (string group in Groups.All)
        {
            var settings = fixture.SettingsFor(group);

            for (int i = 0; i < 20 && await Sub.ProcessMessages<TestMessage>(settings, Token) > 0; i++)
                await Task.Delay(20, Token);
        }

        // The loop above gives up on anything still rent-eligible; take those out of circulation.
        await ResetRentableTasksAsync();
    }

    /// <summary>
    /// Waits until the persisted lease is unambiguously in the past. A fixed <c>Task.Delay</c> races
    /// the second-granularity lease timestamps, and so does a bare "expiry &lt;= now" check: the
    /// delivery path compares against its own <c>now</c>, which lands on the same second and fails the
    /// comparison. Requiring a full extra second of margin removes the guesswork.
    /// </summary>
    private async Task WaitForLeaseExpiryAsync(string payloadId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await TaskLockExpiresOnAsync(payloadId) < UnixNow())
                return;

            await Task.Delay(50, Token);
        }

        Assert.Fail($"The lease for '{payloadId}' did not expire within {timeout}.");
    }

    // ── 2xx: terminal, counted as success, attempt consumed ────────────────

    [Theory]
    [InlineData(Action.Ok, DeliveryStatusCode.Ok)]
    [InlineData(Action.Created, DeliveryStatusCode.Created)]
    [InlineData(Action.Accepted, DeliveryStatusCode.Accepted)]
    [InlineData(Action.NoContent, DeliveryStatusCode.NoContent)]
    [InlineData(Action.Aborted, DeliveryStatusCode.Aborted)]
    public async Task SuccessStatus_IsPersisted_CountedAsHandled_ConsumesAttempt(
        Action action, DeliveryStatusCode expected)
    {
        var id = PayloadId(nameof(SuccessStatus_IsPersisted_CountedAsHandled_ConsumesAttempt), action.ToString());

        // A group per case: the five rows of this theory must not share one consumer group, or the
        // count this test asserts on would include the rows left behind by the cases before it.
        var settings = fixture.SettingsFor($"{Groups.SuccessStatus}_{action}");
        await DrainAsync();

        Plans.Set(id, new Plan(action, TimeSpan.Zero));
        await PublishAsync(id);

        var handled = await Sub.ProcessMessages<TestMessage>(settings, Token);

        // Counted as handled and no longer rent-eligible, so exactly one row.
        Assert.Equal(1, handled);
        Assert.Equal(expected, await TaskStatusAsync(id));
        Assert.Equal(1, await DeliveryRowCountAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(0, await TaskErrorIdAsync(id));

        // A second cycle must not pick it up again: 2xx is terminal.
        Assert.Equal(0, await Sub.ProcessMessages<TestMessage>(settings, Token));
        Assert.Equal(1, await DeliveryRowCountAsync(id));
    }

    // ── Postpone: deliberately attempt-free, still rent-eligible ───────────

    [Fact]
    public async Task Postpone_DoesNotConsumeAttempt_ExtendsLease_StaysRentable()
    {
        var id = PayloadId(nameof(Postpone_DoesNotConsumeAttempt_ExtendsLease_StaysRentable), "p");

        // Long enough that the lease is unambiguously in the future when asserted.
        var delay = TimeSpan.FromSeconds(30);
        Plans.Set(id, new Plan(Action.Postpone, delay));

        var settings = fixture.SettingsFor(Groups.PostponeKeepsTask);
        await DrainAsync();

        int errorsBefore = await TotalErrorRowsAsync();
        await PublishAsync(id);

        // Handled, but not a success and not an error — the count reports handled messages.
        Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(settings, Token));

        Assert.Equal(DeliveryStatusCode.Postpone, await TaskStatusAsync(id));

        // The documented quirk: Postpone is exempt from the attempt counter.
        Assert.Equal(0, await TaskAttemptAsync(id));

        // The delay is applied as the lease extension, so nobody can grab the task before it elapses.
        Assert.True(
            await TaskLockExpiresOnAsync(id) >= UnixNow() + (long)delay.TotalSeconds - 1,
            "task_lock_expires_on must cover the postponement delay");

        // No exception attached -> no error row, and no error id.
        Assert.Equal(0, await TaskErrorIdAsync(id));
        Assert.Equal(errorsBefore, await TotalErrorRowsAsync());
    }

    [Fact]
    public async Task Postpone_TaskIsPickedUpAgain_AfterTheDelayElapses()
    {
        var id = PayloadId(nameof(Postpone_TaskIsPickedUpAgain_AfterTheDelayElapses), "p");

        Plans.Set(id, new Plan(Action.Postpone, TimeSpan.FromSeconds(1)));

        var settings = fixture.SettingsFor(Groups.PostponeRerented);
        await DrainAsync();
        await PublishAsync(id);

        await Sub.ProcessMessages<TestMessage>(settings, Token);
        Assert.Equal(DeliveryStatusCode.Postpone, await TaskStatusAsync(id));

        // Second delivery attempt: a new delivery row, and still no attempt consumed.
        await WaitForLeaseExpiryAsync(id, TimeSpan.FromSeconds(10));
        await Sub.ProcessMessages<TestMessage>(settings, Token);

        Assert.Equal(2, await DeliveryRowCountAsync(id));
        Assert.Equal(0, await TaskAttemptAsync(id));
        Assert.Equal(DeliveryStatusCode.Postpone, await TaskStatusAsync(id));
    }

    // ── Retry: attempt IS consumed ────────────────────────────────────────

    [Fact]
    public async Task Retry_ConsumesAttempt_IsRetryable()
    {
        var id = PayloadId(nameof(Retry_ConsumesAttempt_IsRetryable), "r");

        Plans.Set(id, new Plan(Action.Retry, TimeSpan.FromSeconds(1)));

        var settings = fixture.SettingsFor(Groups.Retry);
        await DrainAsync();
        await PublishAsync(id);

        Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(settings, Token));

        Assert.Equal(DeliveryStatusCode.Retry, await TaskStatusAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(1, await DeliveryRowCountAsync(id));
        Assert.Equal(0, await TaskErrorIdAsync(id));
    }

    // ── Warn: retryable, attempt consumed, NOT logged as an error ──────────

    [Fact]
    public async Task Warn_IsRetryable_ConsumesAttempt_AndIsNotLoggedAsError()
    {
        var id = PayloadId(nameof(Warn_IsRetryable_ConsumesAttempt_AndIsNotLoggedAsError), "w");

        Plans.Set(id, new Plan(Action.Warn, TimeSpan.FromSeconds(1)));

        var settings = fixture.SettingsFor(Groups.Warn);
        await DrainAsync();

        int errorsBefore = await TotalErrorRowsAsync();
        await PublishAsync(id);

        Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(settings, Token));

        Assert.Equal(DeliveryStatusCode.Warn, await TaskStatusAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(1, await DeliveryRowCountAsync(id));

        // The point of filtering GetErrors on IsError(): a transient Warn must not pollute __error$
        // and must not set task.error_id.
        Assert.Equal(0, await TaskErrorIdAsync(id));
        Assert.Equal(errorsBefore, await TotalErrorRowsAsync());
    }

    // ── 5xx: terminal, logged, error id set ───────────────────────────────

    [Fact]
    public async Task Error5xx_IsTerminal_IsLogged_AndSetsErrorId()
    {
        var id = PayloadId(nameof(Error5xx_IsTerminal_IsLogged_AndSetsErrorId), "e");

        Plans.Set(id, new Plan(Action.Error501, TimeSpan.Zero));

        var settings = fixture.SettingsFor(Groups.Error5xx);
        await DrainAsync();

        int errorsBefore = await TotalErrorRowsAsync();
        await PublishAsync(id);

        Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(settings, Token));

        Assert.Equal(DeliveryStatusCode.Error501, await TaskStatusAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(1, await DeliveryRowCountAsync(id));

        // 5xx reaches __error$ and is referenced from the task row. Exactly one new row: the delivery
        // is terminal, and DrainAsync cleared the other groups so nothing re-delivers this message.
        Assert.Equal(errorsBefore + 1, await TotalErrorRowsAsync());
        Assert.Equal(1, await ErrorRowsForAsync(PermanentException.TypeName));
        Assert.NotEqual(0, await TaskErrorIdAsync(id));

        // Terminal: a second cycle must not re-deliver it.
        Assert.Equal(0, await Sub.ProcessMessages<TestMessage>(settings, Token));
        Assert.Equal(1, await DeliveryRowCountAsync(id));
    }

    // ── MovedPermanently: terminal, NOT success, NOT logged ───────────────

    [Fact]
    public async Task MovedPermanently_IsTerminal_ButNeitherSuccessNorError()
    {
        var id = PayloadId(nameof(MovedPermanently_IsTerminal_ButNeitherSuccessNorError), "m");

        Plans.Set(id, new Plan(Action.MovedPermanently, TimeSpan.Zero));

        // 301 is outside IsSuccess() (200..299) and outside IsError(), so it reaches neither the
        // success tally nor the error log — but it is still a message the courier handled.
        // Snapshot the error log first: another test in this class legitimately writes to it, so
        // asserting an absolute count here would depend on the order xUnit happens to run tests in.
        int errorsBefore = await TotalErrorRowsAsync();

        var settings = fixture.SettingsFor(Groups.MovedPermanently);
        await DrainAsync();
        await PublishAsync(id);

        Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(settings, Token));

        Assert.Equal(DeliveryStatusCode.MovedPermanently, await TaskStatusAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(1, await DeliveryRowCountAsync(id));

        // Terminal and nothing written to the error log.
        Assert.Equal(0, await TaskErrorIdAsync(id));
        Assert.Equal(errorsBefore, await TotalErrorRowsAsync());

        // Not re-delivered.
        Assert.Equal(0, await Sub.ProcessMessages<TestMessage>(settings, Token));
        Assert.Equal(1, await DeliveryRowCountAsync(id));
    }

    // ── untouched message is an implicit success ──────────────────────────

    [Fact]
    public async Task UntouchedMessage_IsAutoMarkedOk_AndCounted()
    {
        var id = PayloadId(nameof(UntouchedMessage_IsAutoMarkedOk_AndCounted), "auto");

        // The consumer never touches the context, so the courier must fall back to auto-Ok.
        Plans.Set(id, new Plan(Action.Untouched, TimeSpan.Zero));

        var settings = fixture.SettingsFor(Groups.Untouched);
        await DrainAsync();
        await PublishAsync(id);

        var handled = await Sub.ProcessMessages<TestMessage>(settings, Token);

        Assert.Equal(1, handled);
        Assert.Equal(DeliveryStatusCode.Ok, await TaskStatusAsync(id));
        Assert.Equal(1, await TaskAttemptAsync(id));
        Assert.Equal(0, await TaskErrorIdAsync(id));
    }
}
