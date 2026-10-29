using Sa.Outbox;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Tests;

/// <summary>
/// Batch slicing in <see cref="OutboxMessagePublisher"/>: the rented array is bigger than the
/// logical batch, so the bulk writer must receive the number of items actually written — not the
/// requested length.
/// </summary>
public class OutboxMessagePublisherTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_CollectionReportsMoreItemsThanItYields_WritesOnlyActualItems()
    {
        var writer = new CapturingBulkWriter();
        var publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 16),
            new MetadataConfiguration());

        ulong sent = await publisher.Publish(
            new LyingCollection(["a"]),
            tenantId: 7,
            context: null,
            TestToken);

        Assert.Equal(1UL, sent);
        var only = Assert.Single(writer.Seen);
        Assert.Equal("a", only.Payload);
        Assert.Equal(7, only.TenantId);
    }

    [Fact]
    public async Task Publish_MultipleBatches_KeepsPayloadOrder()
    {
        var writer = new CapturingBulkWriter();
        var publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 2),
            new MetadataConfiguration());

        ulong sent = await publisher.Publish(["a", "b", "c"], tenantId: 1, context: null, TestToken);

        Assert.Equal(3UL, sent);
        Assert.Equal(["a", "b", "c"], writer.Seen.Select(m => m.Payload));
    }

    [Fact]
    public async Task Publish_UsesTheBatchOverload()
    {
        var writer = new BatchCapturingWriter();
        var publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 16),
            new MetadataConfiguration());

        ulong sent = await publisher.Publish(["a", "b"], tenantId: 5, context: null, TestToken);

        Assert.Equal(2UL, sent);
        Assert.Equal(2, writer.Count);
        Assert.Equal(5, writer.Part.TenantId);
        Assert.Equal(string.Empty, writer.FirstPayloadId);
    }

    [Fact]
    public async Task Publish_Batch_SharesOnePartInfo()
    {
        var writer = new CapturingBulkWriter();
        var publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 16),
            new MetadataConfiguration());

        await publisher.Publish(["a", "b"], tenantId: 1, context: null, TestToken);

        Assert.Equal(2, writer.Parts.Count);
        Assert.Equal(writer.Parts[0], writer.Parts[1]);
    }

    [Fact]
    public async Task PublishSingle_ThroughTheInterface_UsesTheSingleMessagePath()
    {
        var writer = new CapturingBulkWriter();
        IOutboxMessagePublisher publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 16),
            new MetadataConfiguration());

        // The 3-argument overload is a default interface method; it must delegate to the
        // 4-argument one so the publisher's single-message implementation is used.
        ulong sent = await publisher.PublishSingle("a", tenantId: 3, TestToken);

        Assert.Equal(1UL, sent);
        var only = Assert.Single(writer.Seen);
        Assert.Equal("a", only.Payload);
        Assert.Equal(3, only.TenantId);
    }

    private sealed class CapturingBulkWriter : IOutboxBulkWriter
    {
        /// <summary>
        /// A copy, not the rented memory: the publisher clears the pooled array on return, so
        /// holding the <see cref="ReadOnlyMemory{T}"/> would read zeroed slots afterwards.
        /// </summary>
        public List<(string? Payload, int TenantId)> Seen { get; } = [];

        public List<OutboxPartInfo> Parts { get; } = [];

        public ValueTask<ulong> InsertBulk<TMessage>(
            ReadOnlyMemory<OutboxMessage<TMessage>> messages,
            CancellationToken cancellationToken = default)
        {
            foreach (OutboxMessage<TMessage> message in messages.Span)
            {
                Seen.Add((
                    (string?)(object?)message.Payload,
                    message.PartInfo.TenantId));
                Parts.Add(message.PartInfo);
            }

            return new ValueTask<ulong>((ulong)messages.Length);
        }
    }

    /// <summary>Implements the batch overload so the test can see whether the publisher used it.</summary>
    private sealed class BatchCapturingWriter : IOutboxBulkWriter
    {
        public int Count { get; private set; }

        public OutboxPartInfo Part { get; private set; }

        public string? FirstPayloadId { get; private set; }

        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
        {
            Count = batch.Count;
            Part = batch.PartInfo;
            FirstPayloadId = batch.Messages.Span[0].PayloadId;
            return new ValueTask<ulong>((ulong)batch.Count);
        }

        public ValueTask<ulong> InsertBulk<TMessage>(
            ReadOnlyMemory<OutboxMessage<TMessage>> messages,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The batch overload was expected.");
    }

    /// <summary>Advertises two items but yields one — the shape a buggy custom collection has.</summary>
    private sealed class LyingCollection(string[] items) : IReadOnlyCollection<string>
    {
        public int Count => items.Length + 1;

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
