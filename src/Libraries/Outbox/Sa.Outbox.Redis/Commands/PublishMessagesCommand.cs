using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Serialization;
using Sa.Outbox.Redis.Services;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Commands;

/// <summary>
/// Appends a publish batch to its stream.
/// </summary>
internal sealed class PublishMessagesCommand(
    IDatabase database,
    RedisOutboxSettings settings,
    IOutboxMessageSerializer serializer,
    RedisOutboxTrimmer trimmer)
{
    public async ValueTask<ulong> ExecuteAsync<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken)
    {
        if (batch.IsEmpty) return 0;

        int tenantId = batch.PartInfo.TenantId;
        string part = batch.PartInfo.Part;
        long createdAt = batch.PartInfo.CreatedAt.ToUnixTimeMilliseconds();

        RedisKey streamKey = settings.KeySettings.StreamKey(tenantId, part, typeof(TMessage).Name);
        string typeName = typeof(TMessage).FullName ?? typeof(TMessage).Name;

        // Pipelined, not atomic: each message is an independent XADD. Nothing here has to hold
        // across messages, so a MULTI/EXEC (or a Lua script) would only add overhead.
        IBatch redisBatch = database.CreateBatch();
        List<Task<RedisValue>> appends = new(batch.Messages.Length);

        foreach (OutboxMessageBatchItem<TMessage> item in batch.Messages.Span)
        {
            NameValueEntry[] fields =
            [
                new(RedisOutboxFields.PayloadId, item.PayloadId),
                new(RedisOutboxFields.PayloadType, typeName),
                new(RedisOutboxFields.CreatedAt, createdAt),
                new(RedisOutboxFields.Payload, OutboxPayloadCodec.Serialize(serializer, item.Payload)),
            ];

            appends.Add(redisBatch.StreamAddAsync(streamKey, fields));
        }

        redisBatch.Execute();

        ulong appended = 0;
        foreach (Task<RedisValue> append in appends)
        {
            _ = await append.ConfigureAwait(false);
            appended++;
        }

        // The tenant list is what IOutboxTenantDetector reports; a set keeps that read cheap.
        await database
            .SetAddAsync(settings.KeySettings.TenantsKey(), tenantId)
            .ConfigureAwait(false);

        // Retention is throttled per stream inside the trimmer, so this is a no-op on most batches.
        await trimmer
            .TrimAfterPublishAsync(streamKey, cancellationToken)
            .ConfigureAwait(false);

        return appended;
    }
}
