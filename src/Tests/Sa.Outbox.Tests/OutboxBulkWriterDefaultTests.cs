using Sa.Outbox;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests;

/// <summary>
/// Verifies the 0.18 <see cref="IOutboxBulkWriter"/> contract: the context-less overload is a
/// default interface method that forwards <c>null</c> to the single abstract batch method, and the
/// legacy per-message overloads no longer exist.
/// </summary>
public class OutboxBulkWriterDefaultTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InsertBulk_WithoutContext_ForwardsNullToTheContextOverload()
    {
        var fake = new FakeBulkWriter();
        IOutboxBulkWriter writer = fake;
        var part = new OutboxPartInfo(1, "part", DateTimeOffset.UtcNow);
        var batch = new OutboxMessageBatch<string>(
            new[] { new OutboxMessageBatchItem<string>("id", "payload") },
            part);

        ulong inserted = await writer.InsertBulk(batch, TestToken);

        Assert.Equal(1UL, inserted);
        Assert.Equal(1, fake.Calls);
        Assert.Null(fake.LastContext);
        Assert.Equal(part, fake.LastPart);
    }

    [Fact]
    public async Task InsertBulk_WithContext_PassesItToTheProvider()
    {
        var fake = new FakeBulkWriter();
        IOutboxBulkWriter writer = fake;
        var part = new OutboxPartInfo(1, "part", DateTimeOffset.UtcNow);
        var batch = new OutboxMessageBatch<string>(
            new[] { new OutboxMessageBatchItem<string>("id", "payload") },
            part);
        object context = new();

        await writer.InsertBulk(batch, context, TestToken);

        Assert.Same(context, fake.LastContext);
    }

    private sealed class FakeBulkWriter : IOutboxBulkWriter
    {
        public int Calls { get; private set; }

        public object? LastContext { get; private set; }

        public OutboxPartInfo LastPart { get; private set; }

        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            LastPart = batch.PartInfo;
            return new ValueTask<ulong>((ulong)batch.Count);
        }
    }
}
