using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Redis.Commands;
using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Globalization;

namespace Sa.Outbox.Redis.Services;

/// <summary>
/// Trims the message streams the provider writes, by a safe floor and age.
/// </summary>
/// <remarks>
/// <para>
/// A message stream is also its consumer groups' history, so it is never trimmed by raw length: an
/// entry may be pending (rented but unacknowledged) or not yet reached by a slow group. The safe
/// floor is the smallest id any group still needs:
/// </para>
/// <list type="bullet">
/// <item>for a group with pending entries — its oldest pending id;</item>
/// <item>otherwise — its <c>last-delivered-id</c>, i.e. everything before it was delivered and
/// acknowledged;</item>
/// <item>a group that has not consumed anything yet (<c>last-delivered-id = 0-0</c>) blocks trimming
/// entirely, because it may still start at the beginning.</item>
/// </list>
/// <para>
/// The entry finally removed is below <c>min(safeFloor, now - StreamMaxAge)</c>: older than the
/// retention window <b>and</b> needed by no group. A stream with no group is not trimmed either — a
/// group created later with <c>StartFrom = Beginning</c> would otherwise start on a hole.
/// </para>
/// <para>
/// Trimming runs inline after a publish batch, throttled per stream by
/// <see cref="RedisOutboxRetentionSettings.TrimInterval"/> so a busy stream does not pay the
/// <c>XINFO</c>/<c>XPENDING</c> round trips on every batch.
/// </para>
/// </remarks>
internal sealed partial class RedisOutboxTrimmer(
    IDatabase database,
    RedisOutboxSettings settings,
    TimeProvider timeProvider,
    ILogger<RedisOutboxTrimmer>? logger = null)
{
    /// <summary>Above this many tracked streams, expired throttle entries are pruned.</summary>
    private const int ThrottlePruneThreshold = 4096;

    private readonly ILogger _logger = logger ?? NullLogger<RedisOutboxTrimmer>.Instance;

    /// <summary>Next allowed trim instant per Redis key, UTC ticks.</summary>
    private readonly ConcurrentDictionary<string, long> _nextTrimAtUtcTicks = new(StringComparer.Ordinal);

    /// <summary>
    /// Trims the message stream if retention is enabled and the throttle allows it. Called after a
    /// publish batch appended to <paramref name="streamKey"/>.
    /// </summary>
    public async ValueTask TrimAfterPublishAsync(
        RedisKey streamKey,
        CancellationToken cancellationToken)
    {
        RedisOutboxRetentionSettings retention = settings.RetentionSettings;

        if (!retention.Enabled || cancellationToken.IsCancellationRequested)
            return;

        DateTimeOffset now = timeProvider.GetUtcNow();

        if (TryBeginTrim(streamKey.ToString(), now))
        {
            try
            {
                await TrimStreamAsync(streamKey, retention, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Retention is best-effort: a trim failure must never fail the publish that
                // triggered it. The throttle already moved forward, so the next attempt is due
                // after TrimInterval.
                LogRetentionFailed(_logger, streamKey.ToString(), ex);
            }
        }
    }

    /// <summary>Trims one message stream down to its safe floor.</summary>
    private async ValueTask TrimStreamAsync(
        RedisKey streamKey,
        RedisOutboxRetentionSettings retention,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StreamGroupInfo[] groups;
        try
        {
            groups = await database
                .StreamGroupInfoAsync(streamKey)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such key", StringComparison.OrdinalIgnoreCase))
        {
            // The stream does not exist (any more): nothing to trim.
            return;
        }

        // No group yet: a group created later may need the whole stream (StartFrom = Beginning).
        if (groups.Length == 0)
            return;

        if (await TryGetSafeFloorAsync(streamKey, groups, cancellationToken).ConfigureAwait(false)
            is not { } floor)
        {
            return;
        }

        RedisValue trimBefore = Format(floor.Milliseconds, floor.Sequence);

        if (retention.StreamMaxAge > TimeSpan.Zero)
        {
            ulong ageCutoff = AgeCutoffMilliseconds(now, retention.StreamMaxAge);
            if (IsBefore(ageCutoff, 0, floor.Milliseconds, floor.Sequence))
            {
                trimBefore = Format(ageCutoff, 0);
            }
        }

        if (string.Equals(trimBefore.ToString(), "0-0", StringComparison.Ordinal))
            return;

        long removed = await database
            .StreamTrimByMinIdAsync(
                streamKey,
                trimBefore,
                retention.Approximate,
                Limit(retention),
                StreamTrimMode.KeepReferences)
            .ConfigureAwait(false);

        if (removed > 0)
        {
            LogStreamTrimmed(_logger, streamKey.ToString(), trimBefore.ToString(), removed);
        }
    }

    /// <summary>
    /// The oldest id any consumer group still needs, or <see langword="null"/> when the stream must
    /// not be trimmed at all.
    /// </summary>
    private async ValueTask<(ulong Milliseconds, ulong Sequence)?> TryGetSafeFloorAsync(
        RedisKey streamKey,
        StreamGroupInfo[] groups,
        CancellationToken cancellationToken)
    {
        ulong floorMilliseconds = 0;
        ulong floorSequence = 0;
        bool hasFloor = false;

        foreach (StreamGroupInfo group in groups)
        {
            RedisValue floor = group.LastDeliveredId;

            if (group.PendingMessageCount > 0)
            {
                StreamPendingInfo pending = await database
                    .StreamPendingAsync(streamKey, group.Name)
                    .ConfigureAwait(false);

                // The count can drop between XINFO and XPENDING (another worker acknowledged the
                // last pending entry); then the delivered cursor is the right floor again.
                floor = pending.PendingMessageCount > 0
                    ? pending.LowestPendingMessageId
                    : group.LastDeliveredId;
            }

            if (!RedisStreamId.TryParse(floor, out ulong milliseconds, out ulong sequence))
                return null;

            // A group that has consumed nothing yet may still start at the beginning.
            if (milliseconds == 0 && sequence == 0)
                return null;

            if (!hasFloor
                || milliseconds < floorMilliseconds
                || (milliseconds == floorMilliseconds && sequence < floorSequence))
            {
                floorMilliseconds = milliseconds;
                floorSequence = sequence;
                hasFloor = true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return hasFloor ? (floorMilliseconds, floorSequence) : null;
    }

    /// <summary>
    /// Returns <see langword="true"/> once per <see cref="RedisOutboxRetentionSettings.TrimInterval"/>
    /// for a key, and <see langword="false"/> while the key is still throttled.
    /// </summary>
    private bool TryBeginTrim(string key, DateTimeOffset now)
    {
        long nowTicks = now.UtcTicks;
        long nextTicks = now.Add(settings.RetentionSettings.TrimInterval).UtcTicks;

        while (true)
        {
            if (!_nextTrimAtUtcTicks.TryGetValue(key, out long dueTicks))
            {
                if (_nextTrimAtUtcTicks.TryAdd(key, nextTicks))
                {
                    PruneThrottleIfNeeded(nowTicks);
                    return true;
                }

                continue;
            }

            if (nowTicks < dueTicks)
                return false;

            if (_nextTrimAtUtcTicks.TryUpdate(key, nextTicks, dueTicks))
                return true;
        }
    }

    private void PruneThrottleIfNeeded(long nowTicks)
    {
        if (_nextTrimAtUtcTicks.Count <= ThrottlePruneThreshold)
            return;

        foreach (KeyValuePair<string, long> entry in _nextTrimAtUtcTicks)
        {
            if (entry.Value <= nowTicks)
            {
                _nextTrimAtUtcTicks.TryRemove(entry);
            }
        }
    }

    private static long? Limit(RedisOutboxRetentionSettings retention)
        => retention.TrimLimit > 0 ? retention.TrimLimit : null;

    private static ulong AgeCutoffMilliseconds(DateTimeOffset now, TimeSpan maxAge)
    {
        long nowMilliseconds = now.ToUnixTimeMilliseconds();
        long ageMilliseconds = (long)maxAge.TotalMilliseconds;

        return ageMilliseconds >= nowMilliseconds ? 0 : (ulong)(nowMilliseconds - ageMilliseconds);
    }

    private static RedisValue Format(ulong milliseconds, ulong sequence)
        => string.Create(CultureInfo.InvariantCulture, $"{milliseconds}-{sequence}");

    private static bool IsBefore(ulong leftMilliseconds, ulong leftSequence, ulong rightMilliseconds, ulong rightSequence)
        => leftMilliseconds < rightMilliseconds
        || (leftMilliseconds == rightMilliseconds && leftSequence < rightSequence);

    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Debug,
        Message = "Trimmed message stream '{Stream}' up to '{TrimBefore}': {Removed} entries removed.")]
    static partial void LogStreamTrimmed(ILogger logger, string stream, string trimBefore, long removed);

    [LoggerMessage(
        EventId = 8103,
        Level = LogLevel.Warning,
        Message = "Retention trim of '{Stream}' failed; publishing and delivery are unaffected and the next attempt is due after TrimInterval.")]
    static partial void LogRetentionFailed(ILogger logger, string stream, Exception exception);
}
