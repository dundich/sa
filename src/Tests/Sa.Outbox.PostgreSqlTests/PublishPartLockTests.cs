using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Data.PostgreSql.Fixture;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSqlTests;

/// <summary>Second message type, mapped to its own part, for the "different parts don't block" case.</summary>
internal sealed class OtherPartMessage
{
    public required string PayloadId { get; set; }
    public int TenantId { get; set; }
}

/// <summary>
/// C1 regression: <c>msg_seq</c> is allocated by <c>nextval</c> at COPY time, but a row only becomes
/// visible at COMMIT. Two publishers of the same <c>(tenant, part)</c> could therefore interleave as
/// "A takes the lower seqs and commits last", and the consumer — whose cursor is
/// <c>max(msg_seq)</c> — would advance past A's rows and never deliver them.
/// </summary>
/// <remarks>
/// Measured before the fix on 8 parallel publishers (batch 1024, caught-up consumer polling every
/// 20 ms): 1.2–7 % of all rows lost, scaling with batch × writers. The publish-side advisory lock on
/// <c>(tenant, part)</c> is what makes seq order equal commit order inside a part, and these tests pin
/// both halves of that: the second publisher must <b>wait</b>, and the resulting seq ranges must not
/// interleave.
/// </remarks>
public sealed class PublishPartLockTests(PublishPartLockTests.Fixture fixture)
    : IClassFixture<PublishPartLockTests.Fixture>
{
    internal const string Schema = "pub_lock";
    internal const int TenantId = 1;

    internal static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date, TimeSpan.Zero);

    public sealed class Fixture : PgDataSourceFixture
    {
        public Fixture()
        {
            Services
                .AddSaOutbox(builder => builder
                    .WithMetadata((_, b) => b
                        .AddMetadata<TestMessage>("root_1", m => m.PayloadId)
                        .AddMetadata<OtherPartMessage>("root_2", m => m.PayloadId))
                    .WithTenants((_, s) => s.WithTenantIds(TenantId))
                    .WithDeliveries(b =>
                        b.AddDeliveryScoped<NoopConsumer, TestMessage>("g1",
                            (_, o) => o.StartImmediately().WithMaxBatchSize(1).WithNoBatchingWindow())))
                .AddSaOutboxUsingPostgreSql(cfg =>
                {
                    cfg.WithDataSource(b => b.Options(ob => ob.Configure(o => o.ConnectionString = ConnectionString)));
                    cfg.WithMessageSerializer(_ => OutboxMessageSerializer.Instance);
                    cfg.WithOutboxSettings((_, s) =>
                    {
                        s.TableSettings.WithSchema(Schema);
                        s.MigrationSettings.AsBackgroundJob = false;
                        s.CleanupSettings.AsBackgroundJob = false;
                    });
                });
        }
    }

    IPgDataSource DataSource => fixture.DataSource;

    CancellationToken Token => TestContext.Current.CancellationToken;

    IOutboxMessagePublisher Publisher => fixture.ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

    PgOutboxTableSettings TableSettings => fixture.ServiceProvider.GetRequiredService<PgOutboxTableSettings>();

    async Task MigrateAsync()
    {
        IMigrationService migration = fixture.ServiceProvider
            .GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);

        int executed = await migration.Migrate([Day], Token);
        Assert.True(executed >= 0, $"migration reported an incomplete run (executed={executed})");
    }

    static TestMessage[] Batch(string prefix, int count)
        => [.. Enumerable.Range(0, count).Select(i => new TestMessage
        {
            PayloadId = $"{prefix}{i}",
            TenantId = TenantId,
        })];

    static OtherPartMessage[] OtherBatch(string prefix, int count)
        => [.. Enumerable.Range(0, count).Select(i => new OtherPartMessage
        {
            PayloadId = $"{prefix}{i}",
            TenantId = TenantId,
        })];

    async Task<long> ScalarAsync(string sql)
        => await DataSource.ExecuteReaderFirst<long>(sql, Token);

    [Fact]
    public async Task PublishInAnotherTransaction_WaitsForThePartLock_SoSeqOrderEqualsCommitOrder()
    {
        await MigrateAsync();

        string msg = TableSettings.GetQualifiedMsgTableName();

        // A: publish inside a transaction that stays open — it holds the part lock and its rows are
        // invisible to everyone else until the commit.
        await using NpgsqlConnection connectionA = await DataSource.OpenDbConnection(Token);
        await using NpgsqlTransaction txA = await connectionA.BeginTransactionAsync(Token);

        ulong sentA = await Publisher.Publish(Batch("t1a-", 10), TenantId, txA, Token);
        Assert.Equal(10UL, sentA);

        // B: publish without an ambient transaction → own transaction, same (tenant, part).
        Task<ulong> publishB = Publisher.Publish(Batch("t1b-", 10), TenantId, context: null, Token).AsTask();

        await Task.Delay(500, Token);
        Assert.False(
            publishB.IsCompleted,
            "the second publish of the same (tenant, part) must wait for the first transaction's lock");

        await txA.CommitAsync(Token);
        Assert.Equal(10UL, await publishB);

        // The property the cursor relies on: every row of the earlier-committed batch is *below*
        // every row of the later one, so a cursor at max(seq) can never skip the former.
        long maxA = await ScalarAsync($"SELECT max(msg_seq) FROM {msg} WHERE msg_payload_id LIKE 't1a-%'");
        long minB = await ScalarAsync($"SELECT min(msg_seq) FROM {msg} WHERE msg_payload_id LIKE 't1b-%'");

        Assert.True(maxA < minB, $"seq ranges interleave: batch A ends at {maxA}, batch B starts at {minB}");

        // Nothing of the earlier batch sits at or above the later one's first row, i.e. the loader's
        // `msg_seq > cursor` predicate delivers every row of both.
        long aBelow = await ScalarAsync(
            $"SELECT count(*) FROM {msg} WHERE msg_payload_id LIKE 't1a-%' AND msg_seq >= {minB}");
        Assert.Equal(0L, aBelow);
    }

    [Fact]
    public async Task PublishersOfDifferentParts_DoNotSerializeEachOther()
    {
        await MigrateAsync();

        // Warm the second part first: creating its partition is a DDL that takes ACCESS EXCLUSIVE on
        // the parent table and would otherwise block on the open transaction below — that is partition
        // management, not the publish lock this test is about.
        await Publisher.Publish(OtherBatch("t2warm-", 1), TenantId, context: null, Token);

        // A holds the lock of (tenant 1, part root_1).
        await using NpgsqlConnection connectionA = await DataSource.OpenDbConnection(Token);
        await using NpgsqlTransaction txA = await connectionA.BeginTransactionAsync(Token);
        await Publisher.Publish(Batch("t2a-", 5), TenantId, txA, Token);

        // A different part must not wait for it: the lock is per (tenant, part), not global.
        Task<ulong> other = Publisher.Publish(OtherBatch("t2c-", 5), TenantId, context: null, Token).AsTask();

        Task completed = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(10), Token));
        Assert.True(ReferenceEquals(other, completed), "publishing another part must not wait for this lock");
        Assert.Equal(5UL, await other);

        await txA.CommitAsync(Token);
    }

    private sealed class NoopConsumer : IConsumer<TestMessage>
    {
        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
