using Sa.Outbox.Delivery;
using Sa.Outbox.Redis.Commands;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Poison;

/// <summary>
/// Builds <see cref="RedisOutboxPoisonMessage"/> from the two places the provider gives up on a
/// message: a stale pending entry and a permanent outcome returned by the consumer.
/// </summary>
internal static class RedisOutboxPoisonMessageFactory
{
    /// <summary>Builds the message from a stream entry that is still present in the log.</summary>
    public static RedisOutboxPoisonMessage FromEntry(
        StreamEntry entry,
        OutboxMessageFilter filter,
        int attempt,
        DeliveryStatusCode statusCode,
        string statusMessage,
        Exception? exception = null)
    {
        return new RedisOutboxPoisonMessage
        {
            OutboxId = RedisStreamId.ToOutboxId(entry.Id),
            PayloadId = (string?)entry[RedisOutboxFields.PayloadId] ?? string.Empty,
            PayloadType = (string?)entry[RedisOutboxFields.PayloadType] ?? filter.PayloadType,
            ConsumerGroup = filter.ConsumerGroupId,
            TenantId = filter.TenantId,
            Part = filter.Part,
            Attempt = attempt,
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            Exception = exception,
            Payload = (byte[]?)entry[RedisOutboxFields.Payload] ?? []
        };
    }

    /// <summary>Builds the message from a delivered context and its serialized payload.</summary>
    public static RedisOutboxPoisonMessage FromContext<TMessage>(
        IOutboxContextOperations<TMessage> context,
        OutboxMessageFilter filter,
        ReadOnlyMemory<byte> payload)
    {
        DeliveryStatusCode statusCode = context.DeliveryResult.Code;
        string statusMessage = context.DeliveryResult.Message;

        return new RedisOutboxPoisonMessage
        {
            OutboxId = context.OutboxId,
            PayloadId = context.PayloadId,
            PayloadType = typeof(TMessage).FullName ?? typeof(TMessage).Name,
            ConsumerGroup = filter.ConsumerGroupId,
            TenantId = filter.TenantId,
            Part = filter.Part,
            Attempt = context.DeliveryInfo.Attempt + 1,
            StatusCode = statusCode,
            StatusMessage = statusMessage,
            Exception = context.Exception,
            Payload = payload
        };
    }
}
