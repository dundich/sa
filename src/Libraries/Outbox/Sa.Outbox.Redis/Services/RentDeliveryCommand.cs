using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Redis.Commands;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Redis.Serialization;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services;

/// <summary>
/// Fills the delivery buffer with the next batch of a consumer group: first stale pending entries
/// that another worker left behind, then new entries.
/// </summary>
/// <remarks>
/// The provider keeps no retry bookkeeping of its own — the stream and its pending entries list
/// (PEL) are the whole state:
/// <list type="number">
/// <item><c>XAUTOCLAIM</c> walks the PEL page by page (<c>NextStartId</c>) and claims entries idle
/// for at least <see cref="RedisOutboxConsumeSettings.PendingClaimIdle"/>;</item>
/// <item><c>XPENDING</c> supplies the delivery counter of the claimed ids; an entry delivered more
/// than <see cref="RedisOutboxConsumeSettings.MaxDeliveryAttempts"/> times is handed to
/// <see cref="Sa.Outbox.Redis.Poison.IRedisOutboxPoisonHandler"/> and acknowledged instead of being delivered
/// again;</item>
/// <item><c>XREADGROUP &gt;</c> fills the rest of the batch with entries no group member has read
/// yet.</item>
/// </list>
/// </remarks>
internal sealed partial class RentDeliveryCommand(
    IDatabase database,
    RedisOutboxSettings settings,
    RedisOutboxGroupRegistry groups,
    IRedisOutboxPoisonHandler poisonHandler,
    IOutboxContextFactory contextFactory,
    IOutboxMessageSerializer serializer,
    ILogger<RentDeliveryCommand>? logger = null)
{
    /// <summary>How many PEL entries one <c>XAUTOCLAIM</c> page may inspect.</summary>
    private const int AutoClaimPageSize = 100;

    private readonly ILogger _logger = logger ?? NullLogger<RentDeliveryCommand>.Instance;

    public async Task<int> ExecuteFillAsync<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        int batchSize = writeBuffer.Length;

        if (batchSize == 0 || cancellationToken.IsCancellationRequested)
            return 0;

        RedisKey streamKey = settings.KeySettings.StreamKey(filter.TenantId, filter.Part, filter.PayloadType);

        await groups
            .EnsureAsync(streamKey, filter.ConsumerGroupId, filter.FromDate, cancellationToken)
            .ConfigureAwait(false);

        // One consumer name per delivery call: ExtendDelivery then addresses exactly this call's
        // pending entries, and a later call never confuses its own entries with them.
        string consumer = filter.TransactId;

        List<StreamEntry> entries = new(batchSize);
        List<int> attempts = new(batchSize);

        await ClaimStaleAsync(streamKey, consumer, batchSize, entries, attempts, filter, cancellationToken)
            .ConfigureAwait(false);

        if (entries.Count < batchSize)
        {
            StreamEntry[] read = await database
                .StreamReadGroupAsync(streamKey, filter.ConsumerGroupId, consumer, ">", batchSize - entries.Count)
                .ConfigureAwait(false);

            foreach (StreamEntry entry in read)
            {
                entries.Add(entry);
                attempts.Add(0);
            }
        }

        if (entries.Count == 0)
            return 0;

        for (int i = 0; i < entries.Count; i++)
        {
            writeBuffer.Span[i] = contextFactory.Create<TMessage>(
                CreateDeliveryMessage<TMessage>(entries[i], attempts[i], filter));
        }

        return entries.Count;
    }

    /// <summary>
    /// Claims pending entries idle for at least <see cref="RedisOutboxConsumeSettings.PendingClaimIdle"/>,
    /// dead-letters those that exceeded the delivery limit and keeps the rest.
    /// </summary>
    private async Task ClaimStaleAsync(
        RedisKey streamKey,
        string consumer,
        int batchSize,
        List<StreamEntry> entries,
        List<int> attempts,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        List<StreamEntry> claimed = new(batchSize);

        RedisValue cursor = "0-0";
        long minIdle = (long)settings.ConsumeSettings.PendingClaimIdle.TotalMilliseconds;

        while (claimed.Count < batchSize)
        {
            int count = Math.Min(batchSize - claimed.Count, AutoClaimPageSize);

            StreamAutoClaimResult page = await database
                .StreamAutoClaimAsync(
                    streamKey,
                    filter.ConsumerGroupId,
                    consumer,
                    minIdle,
                    cursor,
                    count)
                .ConfigureAwait(false);

            if (page.IsNull)
                break;

            foreach (StreamEntry entry in page.ClaimedEntries)
            {
                if (claimed.Count == batchSize)
                    break;

                claimed.Add(entry);
            }

            // NextStartId is "0-0" after the whole PEL has been scanned; otherwise it is where the
            // next XAUTOCLAIM must continue.
            if (page.NextStartId == cursor && page.ClaimedEntries.Length == 0)
                break;

            cursor = page.NextStartId;

            if (cursor == "0-0")
                break;
        }

        if (claimed.Count == 0)
            return;

        // XAUTOCLAIM hands back the entry body but not its delivery counter, so ask XPENDING once
        // for this call's pending entries. The consumer name is unique to this call.
        StreamPendingMessageInfo[] pending = await database
            .StreamPendingMessagesAsync(
                streamKey,
                filter.ConsumerGroupId,
                claimed.Count,
                consumer,
                minId: null,
                maxId: null)
            .ConfigureAwait(false);

        Dictionary<string, int> deliveryCounts = new(pending.Length, StringComparer.Ordinal);
        foreach (StreamPendingMessageInfo info in pending)
            deliveryCounts[info.MessageId.ToString()] = info.DeliveryCount;

        int maxAttempts = settings.ConsumeSettings.MaxDeliveryAttempts;
        List<StreamEntry> poison = [];

        foreach (StreamEntry entry in claimed)
        {
            int deliveryCount = deliveryCounts.TryGetValue(entry.Id.ToString(), out int value) ? value : 1;

            if (deliveryCount > maxAttempts)
            {
                poison.Add(entry);
                continue;
            }

            // Attempt is zero-based for the pipeline: the first delivery (delivery count 1) is
            // attempt 0, exactly as before.
            entries.Add(entry);
            attempts.Add(deliveryCount - 1);
        }

        if (poison.Count == 0)
            return;

        List<RedisValue> acknowledged = new(poison.Count);

        foreach (StreamEntry entry in poison)
        {
            int deliveryCount = deliveryCounts.TryGetValue(entry.Id.ToString(), out int value) ? value : maxAttempts + 1;

            RedisOutboxPoisonMessage message = RedisOutboxPoisonMessageFactory.FromEntry(
                entry,
                filter,
                deliveryCount,
                DeliveryStatusCode.MaximumAttemptsError,
                $"Pending delivery count {deliveryCount} exceeded MaxDeliveryAttempts {maxAttempts}.");

            try
            {
                await poisonHandler.Handle(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The handler refused it (or failed): leave the entry pending so it is claimed and
                // handled again instead of acknowledging work that was not persisted.
                LogPoisonHandlerFailed(_logger, message.PayloadType, message.PayloadId, ex);
                continue;
            }

            acknowledged.Add(entry.Id);
        }

        if (acknowledged.Count > 0)
        {
            await database
                .StreamAcknowledgeAsync(streamKey, filter.ConsumerGroupId, [.. acknowledged])
                .ConfigureAwait(false);
        }
    }

    private OutboxDeliveryMessage<TMessage> CreateDeliveryMessage<TMessage>(
        StreamEntry entry,
        int attempt,
        OutboxMessageFilter filter)
    {
        string payloadId = (string?)entry[RedisOutboxFields.PayloadId] ?? string.Empty;
        long createdAt = (long)entry[RedisOutboxFields.CreatedAt];

        TMessage payload = PayloadCodec.Deserialize<TMessage>(serializer, (byte[]?)entry[RedisOutboxFields.Payload] ?? [])
            ?? throw new InvalidOperationException(
                $"The Redis Outbox entry '{entry.Id}' of type '{typeof(TMessage).FullName}' deserialized to null.");

        var message = new OutboxMessage<TMessage>(
            payloadId,
            payload,
            new OutboxPartInfo(filter.TenantId, filter.Part, DateTimeOffset.FromUnixTimeMilliseconds(createdAt)));

        var deliveryInfo = new OutboxTaskDeliveryInfo(
            TaskId: ToTaskId(entry.Id),
            DeliveryId: attempt,
            Attempt: attempt,
            LastErrorId: 0,
            Status: default,
            PartInfo: new OutboxPartInfo(filter.TenantId, filter.ConsumerGroupId, filter.NowDate));

        return new OutboxDeliveryMessage<TMessage>(RedisStreamId.ToOutboxId(entry.Id), message, deliveryInfo);
    }

    /// <summary>
    /// A monotonic <c>TaskId</c> derived from the stream id: milliseconds scaled by 10^6 plus the
    /// sequence. The pipeline treats it as an opaque identifier of the message.
    /// </summary>
    private static long ToTaskId(RedisValue streamId)
        => RedisStreamId.TryParse(streamId, out ulong milliseconds, out ulong sequence)
            ? (long)(milliseconds * 1_000_000 + (sequence % 1_000_000))
            : 0;

    [LoggerMessage(
        EventId = 8202,
        Level = LogLevel.Error,
        Message = "Redis Outbox poison handler failed for {PayloadType}/{PayloadId}; the entry is left pending and will be claimed again.")]
    static partial void LogPoisonHandlerFailed(ILogger logger, string payloadType, string payloadId, Exception exception);
}
