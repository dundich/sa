using Sa.Outbox;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests;

/// <summary>
/// Verifies the default <see cref="IOutboxBulkWriter"/> contract: the context overload forwards
/// <c>null</c> to the context-less method (unchanged behavior) and fails loudly for providers that
/// do not understand publish contexts.
/// </summary>
public class OutboxBulkWriterDefaultTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InsertBulk_NullContext_ForwardsToContextlessOverload()
    {
        var fake = new FakeBulkWriter();
        IOutboxBulkWriter writer = fake;

        Assert.Equal(1UL, await writer.InsertBulk(Messages(), context: null, TestToken));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task InsertBulk_Batch_ForwardsAsLegacyMessages()
    {
        var fake = new FakeBulkWriter();
        IOutboxBulkWriter writer = fake;
        var part = new OutboxPartInfo(1, "part", DateTimeOffset.UtcNow);
        var batch = new OutboxMessageBatch<string>(
            new[] { new OutboxMessageBatchItem<string>("id", "payload") },
            part);

        Assert.Equal(1UL, await writer.InsertBulk(batch, context: null, TestToken));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void InsertBulk_NonNullContext_ThrowsNotSupported()
    {
        var fake = new FakeBulkWriter();
        IOutboxBulkWriter writer = fake;

        Assert.Throws<NotSupportedException>(() =>
        {
            _ = writer.InsertBulk(Messages(), context: "ctx", cancellationToken: TestToken);
        });

        Assert.Equal(0, fake.Calls);
    }

    private static ReadOnlyMemory<OutboxMessage<string>> Messages()
        => new OutboxMessage<string>[]
        {
            new("id", "payload", new OutboxPartInfo(1, "part", DateTimeOffset.UtcNow)),
        };

    private sealed class FakeBulkWriter : IOutboxBulkWriter
    {
        public int Calls { get; private set; }

        public ValueTask<ulong> InsertBulk<TMessage>(
            ReadOnlyMemory<OutboxMessage<TMessage>> messages,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new ValueTask<ulong>(1);
        }
    }
}