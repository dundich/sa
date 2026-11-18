using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Outbox.Delivery;
using Sa.Outbox.Redis.Commands;
using Sa.Outbox.Redis.Configuration;
using Sa.Outbox.Redis.Poison;
using Sa.Outbox.Serialization;
using StackExchange.Redis;

namespace Sa.Outbox.Redis.Services;

/// <summary>
/// Applies the outcome of one rented batch: acknowledges successes, leaves retryable results in the
/// pending entries list and hands permanent failures to <see cref="IRedisOutboxPoisonHandler"/>.
/// </summary>
/// <remarks>
/// There is no lease set to check against any more. A stale outcome (a worker that came back after
/// its entries were already claimed elsewhere) may acknowledge an entry another worker is processing;
/// delivery is at-least-once, so a consumer must be idempotent either way. Retryable outcomes need no
/// write at all: the entry stays pending and <c>XAUTOCLAIM</c> picks it up once it has been idle for
/// <see cref="RedisOutboxConsumeSettings.PendingClaimIdle"/>.
/// </remarks>
internal sealed partial class FinishDeliveryCommand(
    IDatabase database,
    RedisOutboxSettings settings,
    IRedisOutboxPoisonHandler poisonHandler,
    IOutboxMessageSerializer serializer,
    ILogger<FinishDeliveryCommand>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<FinishDeliveryCommand>.Instance;

    public async Task<int> ExecuteAsync<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (messages.IsEmpty) return 0;

        RedisKey streamKey = settings.KeySettings.StreamKey(filter.TenantId, filter.Part, filter.PayloadType);

        IBatch batch = database.CreateBatch();
        List<Task> operations = [];
        int applied = 0;

        for (int i = 0; i < messages.Length; i++)
        {
            IOutboxContextOperations<TMessage> context = messages.Span[i];
            RedisValue streamId = RedisStreamId.ToStreamId(context.RentToken);

            if (streamId.IsNull)
                continue;

            applied++;
            Outcome outcome = Outcome.Of(context);

            if (outcome.Kind == OutcomeKind.Settled)
            {
                operations.Add(batch.StreamAcknowledgeAsync(streamKey, filter.ConsumerGroupId, streamId));
            }
            else if (outcome.Kind == OutcomeKind.Permanent)
            {
                RedisOutboxPoisonMessage poison = RedisOutboxPoisonMessageFactory.FromContext(
                    context,
                    filter,
                    OutboxPayloadCodec.Serialize(serializer, context.Payload));

                try
                {
                    await poisonHandler.HandleAsync(poison, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave the entry pending: the outcome was not persisted anywhere, so
                    // acknowledging it would lose the message.
                    LogPoisonHandlerFailed(_logger, poison.PayloadType, poison.PayloadId, ex);
                    continue;
                }

                operations.Add(batch.StreamAcknowledgeAsync(streamKey, filter.ConsumerGroupId, streamId));
            }

            // Retryable (Warn/Retry/Postpone/Pending) and Postpone need no command: the entry stays
            // in the PEL and becomes claimable again after PendingClaimIdle.
        }

        batch.Execute();
        await Task.WhenAll(operations).ConfigureAwait(false);

        return applied;
    }

    [LoggerMessage(
        EventId = 8203,
        Level = LogLevel.Error,
        Message = "Redis Outbox poison handler failed for {PayloadType}/{PayloadId}; the entry is left pending and will be claimed again.")]
    static partial void LogPoisonHandlerFailed(ILogger logger, string payloadType, string payloadId, Exception exception);

    private enum OutcomeKind
    {
        Settled = 0,
        Retryable = 1,
        Permanent = 2,
    }

    private readonly record struct Outcome(OutcomeKind Kind)
    {
        public static Outcome Of(IOutboxContext context)
        {
            DeliveryStatusCode code = context.DeliveryResult.Code;

            if (code.IsError())
                return new Outcome(OutcomeKind.Permanent);

            if (code.IsWarning() || code.IsPostponed() || code.IsRetry() || code.IsPending())
                return new Outcome(OutcomeKind.Retryable);

            return new Outcome(OutcomeKind.Settled);
        }
    }
}
