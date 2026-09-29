using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// C2 regression: the consumption cursor is <c>msg_seq</c> (BIGSERIAL — database-assigned
/// insertion order), not the application-minted v7 <c>msg_id</c>.
/// </summary>
/// <remarks>
/// Here the id generator is steered so the second batch's ids sort *below* the cursor the first
/// batch left behind (a skewed/backdated application clock). Under the old <c>msg_id &gt; @offset</c>
/// cursor those messages were skipped forever; under <c>msg_seq &gt; @offset</c> they must be
/// delivered, because the database assigns insertion order independently of writer clocks.
/// The SQL asserts prove the mechanism: msg_seq is filled by COPY (BIGSERIAL default) and is
/// strictly increasing even though msg_id order is the reverse of insertion order.
/// </remarks>
public class DeliveryCursorMsgSeqTests(DeliveryCursorMsgSeqTests.Fixture fixture)
    : IClassFixture<DeliveryCursorMsgSeqTests.Fixture>
{
    internal sealed class C2Consumer : IConsumer<TestMessage>
    {
        public static int Counter;

        public static void Reset() => Counter = 0;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref Counter, messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Returns ids whose timestamp component has nothing to do with the write moment — exactly
    /// what a skewed or backdated clock does in production.
    /// </summary>
    internal sealed class SteerableIdGenerator : IOutboxIdGenerator
    {
        public DateTimeOffset IdTime { get; set; } = DateTimeOffset.UtcNow;

        public Guid GenId(DateTimeOffset timestamp) => Guid.CreateVersion7(IdTime);
    }

    public class Fixture : OutboxPostgreSqlFixture<IOutboxDeliveryManager>
    {
        internal const string GroupId = "c2_msg_seq";
        internal const int TenantId = 1;

        internal SteerableIdGenerator IdGen { get; } = new();

        public Fixture() : base()
        {
            // Registered after the base ctor ran AddSaOutboxUsingPostgreSql, whose IdGen setup
            // does RemoveAll+AddSingleton — the last registration wins, so this replaces it.
            Services.AddSingleton<IOutboxIdGenerator>(IdGen);

            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, b) => b.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                .WithTenants((_, s) => s.WithTenantIds(TenantId))
                .WithDeliveries(deliveryBuilder => deliveryBuilder
                    .AddDeliveryScoped<C2Consumer, TestMessage>(GroupId, (_, b) =>
                    {
                        b.WithInterval(TimeSpan.FromMilliseconds(100)).WithNoBatchingWindow();
                    })
                )
            );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public PgOutboxTableSettings TableSettings => ServiceProvider.GetRequiredService<PgOutboxTableSettings>();
    }


    [Fact]
    public async Task SecondPublish_WithIdsBelowCursor_IsStillDelivered()
    {
        C2Consumer.Reset();
        var ct = TestContext.Current.CancellationToken;

        var scheduler = fixture.ServiceProvider.GetRequiredService<IScheduler>();
        Assert.True(await scheduler.Start(ct) >= 1);

        var publisher = fixture.Publisher;

        // Batch A: ids minted by a clock two hours in the future.
        var skew = DateTimeOffset.UtcNow.AddHours(2);
        fixture.IdGen.IdTime = skew;

        var batchA = new[]
        {
            new TestMessage { PayloadId = "a1", TenantId = Fixture.TenantId },
            new TestMessage { PayloadId = "a2", TenantId = Fixture.TenantId },
            new TestMessage { PayloadId = "a3", TenantId = Fixture.TenantId },
        };
        ulong sentA = await publisher.Publish(batchA, Fixture.TenantId, ct);
        Assert.Equal((ulong)batchA.Length, sentA);

        await WaitForAsync(() => C2Consumer.Counter >= batchA.Length, ct);

        // Batch B: ids minted by a clock set *before* batch A — every id sorts below the cursor
        // the first batch advanced to. A backdated publisher behaves exactly like this.
        fixture.IdGen.IdTime = skew.AddHours(-3);

        var batchB = new[]
        {
            new TestMessage { PayloadId = "b1", TenantId = Fixture.TenantId },
            new TestMessage { PayloadId = "b2", TenantId = Fixture.TenantId },
        };
        ulong sentB = await publisher.Publish(batchB, Fixture.TenantId, ct);
        Assert.Equal((ulong)batchB.Length, sentB);

        await WaitForAsync(() => C2Consumer.Counter >= batchA.Length + batchB.Length, ct);
        await scheduler.Stop();

        // The regression itself: with the old msg_id cursor batch B would never be seen.
        Assert.Equal(batchA.Length + batchB.Length, C2Consumer.Counter);

        await AssertMsgSeqIsDbAssignedOrder(ct);
    }

    /// <summary>
    /// The mechanism the fix relies on, pinned at the data level: COPY fills msg_seq via the
    /// BIGSERIAL default (contiguous 1..N in insertion order), while msg_id order is the
    /// *reverse* of insertion order — which is exactly why the id cursor lost messages.
    /// </summary>
    private async Task AssertMsgSeqIsDbAssignedOrder(CancellationToken ct)
    {
        var tableSettings = fixture.TableSettings;
        var fields = tableSettings.Message.Fields;

        var seqBySeq = await fixture.DataSource.ExecuteReaderList<long>(
            $"SELECT {fields.MsgSeq} FROM {tableSettings.GetQualifiedMsgTableName()} WHERE {fields.TenantId} = {Fixture.TenantId} ORDER BY {fields.MsgSeq}",
            r => r.GetInt64(0), ct);

        var seqById = await fixture.DataSource.ExecuteReaderList<long>(
            $"SELECT {fields.MsgSeq} FROM {tableSettings.GetQualifiedMsgTableName()} WHERE {fields.TenantId} = {Fixture.TenantId} ORDER BY {fields.MsgId}",
            r => r.GetInt64(0), ct);

        Assert.Equal(5, seqBySeq.Count);
        // BIGSERIAL filled by COPY, strictly increasing in insertion order.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqBySeq);
        // Insertion order != id order — the ordering the msg_seq cursor is built on.
        Assert.NotEqual(seqBySeq, seqById);

        long groupOffset = await fixture.DataSource.ExecuteReaderFirst<long>(
            $"SELECT {tableSettings.Offset.Fields.GroupOffsetSeq} FROM {tableSettings.GetQualifiedOffsetTableName()} WHERE {tableSettings.Offset.Fields.ConsumerGroup} = '{Fixture.GroupId}'",
            ct);

        // The whole batch was consumed: cursor is at the last seq, not at the "newest" id.
        Assert.Equal(5, groupOffset);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int maxAttempts = 40)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
            await Task.Delay(200, ct);

        Assert.True(condition(), "condition not met within the wait budget");
    }
}


/// <summary>
/// C2 floor semantics: <c>WithMinOffset(group, DateTimeOffset)</c> is translated once into an
/// exclusive <c>msg_seq</c> boundary — the last message created strictly before the floor — so
/// the first message at/after the floor is the first delivered. m1 is published before the
/// floor and must stay undelivered; m2 (published after the floor) must be delivered even though
/// both carry ordinary ids.
/// </summary>
public class DeliveryMinOffsetFloorTests(DeliveryMinOffsetFloorTests.Fixture fixture)
    : IClassFixture<DeliveryMinOffsetFloorTests.Fixture>
{
    internal sealed class FloorConsumer : IConsumer<TestMessage>
    {
        public static int Counter;

        public static void Reset() => Counter = 0;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            Interlocked.Add(ref Counter, messages.Length);
            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : OutboxPostgreSqlFixture<IOutboxDeliveryManager>
    {
        internal const string GroupId = "c2_floor";
        internal const int TenantId = 2;

        public Fixture() : base()
        {
            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, b) => b.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                .WithTenants((_, s) => s.WithTenantIds(TenantId))
                .WithDeliveries(deliveryBuilder => deliveryBuilder
                    .AddDeliveryScoped<FloorConsumer, TestMessage>(GroupId, (_, b) =>
                    {
                        b.WithInterval(TimeSpan.FromMilliseconds(100)).WithNoBatchingWindow();
                    })
                )
            );
        }

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public PgOutboxTableSettings TableSettings => ServiceProvider.GetRequiredService<PgOutboxTableSettings>();
    }


    [Fact]
    public async Task FloorByDate_SkipsMessagesBeforeIt()
    {
        FloorConsumer.Reset();
        var ct = TestContext.Current.CancellationToken;

        var publisher = fixture.Publisher;

        // The scheduler is deliberately not started yet: every published message must be in the
        // msg table before the floor is configured and the group first consumes.
        var t0 = DateTimeOffset.UtcNow;
        ulong sentM1 = await publisher.PublishSingle(
            new TestMessage { PayloadId = "below-floor", TenantId = Fixture.TenantId }, Fixture.TenantId, ct);
        Assert.Equal(1UL, sentM1);

        // The floor sits one whole second after m1's (second-truncated) creation moment, and m2
        // is written at least one second later — so with second truncation the ordering
        // below/above the floor is guaranteed regardless of sub-second jitter.
        await Task.Delay(1100, ct);

        var floor = DateTimeOffset.FromUnixTimeSeconds(t0.ToUnixTimeSeconds() + 1);

        // Configure mid-test: PgOutboxSettings is a singleton, the loader resolves the floor
        // lazily on the group's first load (inside the advisory lock) and caches it.
        var outboxSettings = fixture.ServiceProvider.GetRequiredService<PgOutboxSettings>();
        outboxSettings.ConsumeSettings.WithMinOffset(Fixture.GroupId, floor);

        ulong sentM2 = await publisher.PublishSingle(
            new TestMessage { PayloadId = "at-or-above-floor", TenantId = Fixture.TenantId }, Fixture.TenantId, ct);
        Assert.Equal(1UL, sentM2);

        var scheduler = fixture.ServiceProvider.GetRequiredService<IScheduler>();
        Assert.True(await scheduler.Start(ct) >= 1);

        await WaitForAsync(() => FloorConsumer.Counter >= 1, ct);

        // Give the group a chance to (wrongly) deliver m1, then prove it never did.
        await Task.Delay(600, ct);
        await scheduler.Stop();

        Assert.Equal(1, FloorConsumer.Counter);

        var tableSettings = fixture.TableSettings;
        var fields = tableSettings.Message.Fields;

        // m1 still exists in the msg table — it is below the floor by design, not lost.
        long msgCount = await fixture.DataSource.ExecuteReaderFirst<long>(
            $"SELECT COUNT(*) FROM {tableSettings.GetQualifiedMsgTableName()} WHERE {fields.TenantId} = {Fixture.TenantId}",
            ct);
        Assert.Equal(2, msgCount);

        // The cursor sits at m2's seq (2), not at 0 and not at m1's seq: the resolved exclusive
        // boundary (1) was consumed and advanced to the floor message itself.
        long groupOffset = await fixture.DataSource.ExecuteReaderFirst<long>(
            $"SELECT {tableSettings.Offset.Fields.GroupOffsetSeq} FROM {tableSettings.GetQualifiedOffsetTableName()} WHERE {tableSettings.Offset.Fields.ConsumerGroup} = '{Fixture.GroupId}'",
            ct);
        Assert.Equal(2, groupOffset);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct, int maxAttempts = 40)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
            await Task.Delay(200, ct);

        Assert.True(condition(), "condition not met within the wait budget");
    }
}