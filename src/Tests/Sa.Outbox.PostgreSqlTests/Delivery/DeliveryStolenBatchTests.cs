using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// A batch whose lock has been taken by somebody else by the time the original worker tries
/// to acknowledge it must be reported, not silently dropped.
/// </summary>
/// <remarks>
/// The steal is performed with a direct UPDATE rather than by letting a second
/// <see cref="IDeliveryProcessor"/> race for the same rows. Two reasons:
/// the library's own lock renewer is guaranteed to keep a batch alive for as long as
/// <c>Consume</c> runs, so a timing-based steal would need the test to defeat a mechanism
/// that is working correctly; and a race would make the test flaky rather than deterministic.
/// Overwriting <c>task_transact_id</c> is exactly the state a real steal leaves behind, and
/// the UPDATE is the same one <c>SqlLockAndSelect</c>'s <c>updated_tasks</c> CTE performs when
/// a second worker takes an expired lock.
/// </remarks>
public sealed class DeliveryStolenBatchTests(DeliveryStolenBatchTests.Fixture fixture)
    : IClassFixture<DeliveryStolenBatchTests.Fixture>
{
    /// <summary>
    /// Matches on a fragment of the message rather than on the event id, so the assertion is
    /// about the claim being made and survives a renumbering of the log event.
    /// </summary>
    private const string WarningFragment = "lost the lock to another worker";

    private const int StolenTenantId = 1;
    private const int IntactTenantId = 2;


    public class Fixture : OutboxPostgreSqlFixture<IOutboxDeliveryManager>
    {
        internal RecordingLogger<FinishDeliveryCommand> Logger { get; } = new();

        public Fixture() : base()
        {
            // A closed registration beats the open-generic NullLogger<> that SaFixture installs
            // by default, and is the only way FinishDeliveryCommand's optional
            // ILogger<FinishDeliveryCommand> is ever non-null in a test.
            Services.AddSingleton<ILogger<FinishDeliveryCommand>>(Logger);

            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, b) => b.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                // One tenant per test. A fresh consumer group starts from offset 0 and so
                // picks up *every* message of its tenant, so sharing a tenant would make the
                // second test also rent the first test's message.
                .WithTenants((_, s) => s.WithTenantIds(StolenTenantId, IntactTenantId))
            );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public PgOutboxTableSettings TableSettings => ServiceProvider.GetRequiredService<PgOutboxTableSettings>();
    }


    private IOutboxDeliveryManager Manager => fixture.Sub;

    private static string Describe(IReadOnlyList<LogEntry> entries)
        => entries.Count == 0
            ? "(nothing logged)"
            : string.Join(" | ", entries.Select(e => $"[{e.Level}] {e.Message}"));

    private static bool WarnedAboutSteal(IReadOnlyList<LogEntry> entries)
        => entries.Any(e => e.Level == LogLevel.Warning
            && e.Message.Contains(WarningFragment, StringComparison.Ordinal));


    [Fact]
    public async Task Return_StolenBatch_LogsWarning_AndReportsZeroUpdated()
    {
        var ct = TestContext.Current.CancellationToken;

        var published = await fixture.Publisher.Publish(
            [new TestMessage { PayloadId = "11", Content = "Stolen", TenantId = StolenTenantId }],
            m => m.TenantId,
            ct);
        Assert.True(published > 0);

        var now = DateTimeOffset.UtcNow;

        // Both tests share the fixture and therefore the logger, so assert only on what this
        // test itself caused to be logged.
        var mark = fixture.Logger.Mark;

        // The filter carries the transaction id that FinishDelivery will insist on matching, so
        // it must be the same filter used to rent.
        var filter = new OutboxMessageFilter(
            TransactId: "test-stolen-batch-1",
            ConsumerGroupId: "test1",
            PayloadType: nameof(TestMessage),
            TenantId: StolenTenantId,
            Part: "root_1",
            FromDate: new DateTimeOffset(now.Date.AddDays(-1), now.Offset),
            ToDate: now,
            NowDate: now);

        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        var rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromSeconds(30), filter, ct);
        Assert.Equal(1, rented);

        var rentedBatch = buffer.AsMemory(0, rented);

        await StealAsync(filter, ct);

        var updated = await Manager.ReturnDelivery<TestMessage>(rentedBatch, filter, ct);

        // A stolen batch acknowledges nothing — the new owner holds the rows, and the old
        // owner's UPDATE matches on task_transact_id.
        Assert.Equal(0, updated);

        var logged = fixture.Logger.Since(mark);

        Assert.True(
            WarnedAboutSteal(logged),
            $"expected a warning containing '{WarningFragment}', got: {Describe(logged)}");

        // Nothing threw: losing a lease is not a failure, it is at-least-once re-delivery, and
        // the warning must not be dressed up as an exception.
        Assert.DoesNotContain(logged, e => e.Exception != null);
    }


    [Fact]
    public async Task Return_WholeBatch_LogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var published = await fixture.Publisher.Publish(
            [new TestMessage { PayloadId = "21", Content = "Intact", TenantId = IntactTenantId }],
            m => m.TenantId,
            ct);
        Assert.True(published > 0);

        var now = DateTimeOffset.UtcNow;

        var mark = fixture.Logger.Mark;

        var filter = new OutboxMessageFilter(
            TransactId: "test-stolen-batch-2",
            ConsumerGroupId: "test2",
            PayloadType: nameof(TestMessage),
            TenantId: IntactTenantId,
            Part: "root_1",
            FromDate: new DateTimeOffset(now.Date.AddDays(-1), now.Offset),
            ToDate: now,
            NowDate: now);

        IOutboxContextOperations<TestMessage>[] buffer = new IOutboxContextOperations<TestMessage>[4];

        var rented = await Manager.RentDelivery<TestMessage>(buffer, TimeSpan.FromSeconds(30), filter, ct);
        Assert.Equal(1, rented);

        var updated = await Manager.ReturnDelivery<TestMessage>(buffer.AsMemory(0, rented), filter, ct);

        Assert.Equal(1, updated);

        // The counter-test: a batch that was never stolen must stay silent, so the warning is
        // evidence of a lost lease rather than a permanent fixture of the command.
        var logged = fixture.Logger.Since(mark);
        Assert.False(
            WarnedAboutSteal(logged),
            $"a whole batch acknowledged cleanly, got: {Describe(logged)}");
    }


    /// <summary>
    /// Rewrites the batch's <c>task_transact_id</c>, which is the same mutation a competing
    /// worker's <c>SqlLockAndSelect</c> performs when it takes over an expired lock.
    /// </summary>
    private async Task StealAsync(OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        var settings = fixture.TableSettings;
        var fields = settings.TaskQueue.Fields;

        var sql =
            $"""
            UPDATE {settings.GetQualifiedTaskTableName()}
            SET {fields.TaskTransactId} = 'stolen-by-another-worker'
            WHERE
              {fields.TenantId} = @tnt
              AND {fields.ConsumerGroup} = @gr
              AND {fields.TaskTransactId} = @trn
            """;

        var affected = await fixture.DataSource.ExecuteNonQuery(sql, cmd =>
        {
            cmd.Parameters.Add(new NpgsqlParameter<int>(SqlParamNames.TenantId, filter.TenantId));
            cmd.Parameters.Add(new NpgsqlParameter<string>(SqlParamNames.ConsumerGroup, filter.ConsumerGroupId));
            cmd.Parameters.Add(new NpgsqlParameter<string>(SqlParamNames.TransactId, filter.TransactId));
        },
            cancellationToken);

        Assert.Equal(1, affected);
    }

    private static class SqlParamNames
    {
        public const string TenantId = "@tnt";
        public const string ConsumerGroup = "@gr";
        public const string TransactId = "@trn";
    }
}
