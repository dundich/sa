using System.Globalization;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Tests;

/// <summary>
/// Value-type payloads: metadata registration and the tenant-deriving publish overloads must
/// accept structs, not only classes.
/// </summary>
public class ValueTypePublishableTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private readonly record struct StructMessage(int Id, int TenantId) : IOutboxPublishable
    {
        public string GetPayloadId() => Id.ToString(CultureInfo.InvariantCulture);

        public int GetTenantId() => TenantId;

        public static string PartName => "root";
    }

    [Fact]
    public async Task StructPayload_RegistersMetadataAndPublishesThroughEveryOverload()
    {
        var writer = new CountingBulkWriter();
        var metadata = new MetadataConfiguration();
        ((IOutboxMessageMetadataBuilder)metadata).AddMetadata<StructMessage>();

        IOutboxMessagePublisher publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 16),
            metadata);

        StructMessage[] messages =
        [
            new StructMessage(1, TenantId: 5),
            new StructMessage(2, TenantId: 5),
        ];

        Assert.Equal(2UL, await publisher.Publish(messages, tenantId: 5, context: null, TestToken));
        Assert.Equal(2UL, await publisher.Publish(messages, TestToken));
        Assert.Equal(1UL, await publisher.PublishSingle(messages[0], tenantId: 5, TestToken));

        Assert.Equal(5, writer.Count);
    }

    private sealed class CountingBulkWriter : IOutboxBulkWriter
    {
        public int Count { get; private set; }

        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
        {
            Count += batch.Count;
            return new ValueTask<ulong>((ulong)batch.Count);
        }

        public ValueTask<ulong> InsertBulk<TMessage>(
            ReadOnlyMemory<OutboxMessage<TMessage>> messages,
            CancellationToken cancellationToken = default)
        {
            Count += messages.Length;
            return new ValueTask<ulong>((ulong)messages.Length);
        }
    }
}
