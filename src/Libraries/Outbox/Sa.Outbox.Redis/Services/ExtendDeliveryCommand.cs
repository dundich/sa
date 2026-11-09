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
    /// <summary>
    /// Page size of one <c>XPENDING</c> scan. The scan continues until a short page, so a delivery
    /// that rented more entries than this still renews all of them.
    /// </summary>
    private const int PendingScanPageSize = 1024;

    public async Task<int> ExecuteAsync(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;

        RedisKey streamKey = settings.KeySettings.StreamKey(filter.TenantId, filter.Part, filter.PayloadType);
        string consumer = filter.TransactId;

        // A delivery may rent more entries than one XPENDING page, and every one of them has to
        // have its idle clock reset: an un-renewed tail can be claimed by XAUTOCLAIM while the
        // current worker is still processing it. XPENDING accepts an exclusive lower bound, so
        // each page resumes right after the previous page's last id.
        List<RedisValue> ids = [];
        RedisValue? minId = null;

        while (true)
        {
            StreamPendingMessageInfo[] pending = await database
                .StreamPendingMessagesAsync(
                    streamKey,
                    filter.ConsumerGroupId,
                    PendingScanPageSize,
                    consumer,
                    minId,
                    maxId: null)
                .ConfigureAwait(false);

            if (pending.Length == 0)
                break;

            foreach (StreamPendingMessageInfo info in pending)
                ids.Add(info.MessageId);

            if (pending.Length < PendingScanPageSize || cancellationToken.IsCancellationRequested)
                break;

            minId = "(" + pending[^1].MessageId.ToString();
        }

        if (ids.Count == 0)
            return 0;

        // JUSTID: reset the idle clock, do not consume a delivery attempt.
        RedisValue[] renewed = await database
            .StreamClaimIdsOnlyAsync(streamKey, filter.ConsumerGroupId, consumer, 0, [.. ids])
            .ConfigureAwait(false);

        return renewed.Length;
    }
}
