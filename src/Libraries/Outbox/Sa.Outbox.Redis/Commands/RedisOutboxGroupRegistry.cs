using Sa.Outbox.Redis.Configuration;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Sa.Outbox.Redis.Commands;

/// <summary>
/// Creates the Redis consumer group of a (stream, consumer group) pair on first use, at the start
/// position required by <see cref="RedisOutboxConsumeSettings.StartFrom"/>.
/// </summary>
/// <remarks>
/// A created group is remembered in-process, so the check is not a round trip on every poll. The
/// group lives in the stream and is never removed by the provider.
/// </remarks>
internal sealed class RedisOutboxGroupRegistry(IDatabase database, RedisOutboxSettings settings)
{
    private readonly ConcurrentDictionary<(string Stream, string Group), byte> _known = new();

    public async ValueTask EnsureAsync(
        RedisKey stream,
        RedisValue group,
        DateTimeOffset lookbackFloor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (stream.ToString(), group.ToString());

        if (_known.ContainsKey(key))
            return;

        try
        {
            await database
                .StreamCreateConsumerGroupAsync(stream, group, ResolveStart(lookbackFloor), createStream: true)
                .ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.Ordinal))
        {
            // Another instance won the race — the group exists, which is all we need.
        }

        _known[key] = 1;
    }

    private RedisValue ResolveStart(DateTimeOffset lookbackFloor)
    {
        return settings.ConsumeSettings.StartFrom switch
        {
            RedisOutboxGroupStart.Beginning => "0-0",
            RedisOutboxGroupStart.Now => "$",
            _ => lookbackFloor == default ? "0-0" : $"{lookbackFloor.ToUnixTimeMilliseconds()}-0",
        };
    }
}
