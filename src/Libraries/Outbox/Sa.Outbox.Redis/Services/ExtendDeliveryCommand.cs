using Sa.Outbox.Delivery;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services;

/// <summary>
/// Resets the idle time of the entries rented by one delivery call, so a slow batch is not stolen
/// while it is still being processed.
/// </summary>
/// <remarks>
/// The consumer name of a delivery call is its <c>TransactId</c>, so <c>XPENDING</c> lists exactly
/// the entries this call still owns. <c>XCLAIM … JUSTID</c> then sets their last-delivery time to
/// now without incrementing the delivery counter — a renewal is not a delivery attempt.
/// </remarks>
internal sealed class ExtendDeliveryCommand(IDatabase database, RedisOutboxSettings settings)
{
    /// <summary>The largest pending batch the renewal scans. A batch larger than this is a configuration error.</summary>
    private const int PendingScanSize = 1024;

    public async Task<int> ExecuteAsync(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;

        RedisKey streamKey = settings.KeySettings.StreamKey(filter.TenantId, filter.Part, filter.PayloadType);
        string consumer = filter.TransactId;

        StreamPendingMessageInfo[] pending = await database
            .StreamPendingMessagesAsync(
                streamKey,
                filter.ConsumerGroupId,
                PendingScanSize,
                consumer,
                minId: null,
                maxId: null)
            .ConfigureAwait(false);

        if (pending.Length == 0)
            return 0;

        RedisValue[] ids = new RedisValue[pending.Length];
        for (int i = 0; i < pending.Length; i++)
            ids[i] = pending[i].MessageId;

        // JUSTID: reset the idle clock, do not consume a delivery attempt.
        RedisValue[] renewed = await database
            .StreamClaimIdsOnlyAsync(streamKey, filter.ConsumerGroupId, consumer, 0, ids)
            .ConfigureAwait(false);

        return renewed.Length;
    }
}
