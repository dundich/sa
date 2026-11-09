using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sa.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.Serialization;
using System.Collections.ObjectModel;

namespace Sa.Outbox.PostgreSql.Services.Plug;


internal sealed partial class OutboxDeliveryManager(
    IStartDeliveryCommand startCmd
    , IErrorDeliveryCommand errorCmd
    , IFinishDeliveryCommand finishCmd
    , IExtendDeliveryCommand extendCmd
    , IOutboxPartRepository partRepository
    , IOutboxTaskLoader loader
    , IOutboxMessageSerializer serializer
    , IPgOutboxPoisonHandler poisonHandler
    , ILogger<OutboxDeliveryManager>? logger = null
) : IOutboxDeliveryManager
{
    private readonly ILogger _logger = logger ?? NullLogger<OutboxDeliveryManager>.Instance;
    public async Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        var batchSize = writeBuffer.Length;

        if (cancellationToken.IsCancellationRequested || batchSize == 0) return 0;

        await EnsureParts(filter, cancellationToken);

        var _ = await loader.LoadNewTasks(filter, batchSize, cancellationToken);

        // новых заданий может и не быть... продолжаем обработку старых
        return await startCmd.ExecuteFill(writeBuffer, lockDuration, filter, cancellationToken);
    }

    public async Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        // One pass over the batch (M7): error contexts and delivery-part keys are derived here,
        // each deduplicated as it goes. Delivery parts are keyed by day (M6): the partition is
        // daily, so the exact timestamp is noise that used to fan one batch into several EnsureParts
        // round trips.
        //
        // The parts are kept in a dictionary keyed by the same (day, tenant, part) tuple the
        // repository dedupes by, so the record is copied once per *distinct* part instead of once
        // per message. The old HashSet.Add(partInfo with {...}) allocated a new OutboxPartInfo for
        // every delivered message, which is the common case: one part repeated across the batch.
        List<IOutboxContextOperations<TMessage>>? errs = null;
        Dictionary<(DateTimeOffset Day, int TenantId, string Part), OutboxPartInfo> deliveryParts = new();

        foreach (IOutboxContextOperations<TMessage> context in messages.Span)
        {
            var day = context.DeliveryResult.CreatedAt.StartOfDay();
            OutboxPartInfo part = context.DeliveryInfo.PartInfo;
            var partKey = (day, part.TenantId, part.Part);

            if (!deliveryParts.ContainsKey(partKey))
            {
                deliveryParts[partKey] = part with { CreatedAt = day };
            }

            if (context.Exception is not null && context.DeliveryResult.Code.IsError())
            {
                (errs ??= []).Add(context);
            }
        }

        // The interceptor runs before any database write and picks the strategy per message:
        //   Default  -> keep the built-in __error$ insert;
        //   Handled  -> the handler owns the error record, skip __error$;
        //   throwing -> leave the task Processing so it is re-rented and the handler runs again.
        List<IOutboxContextOperations<TMessage>>? errorLogs = errs is null
            ? null
            : await InterceptPoisonHandlersAsync(errs, cancellationToken).ConfigureAwait(false);

        HashSet<DateTimeOffset> errorDays = errorLogs is null
            ? []
            : [.. errorLogs.Select(context => context.DeliveryResult.CreatedAt.StartOfDay())];

        // Delivery-log and error-log partitions are independent DDL on different tables — run the
        // ensures concurrently instead of awaiting them in sequence (M6).
        Task<int> ensureDelivery = partRepository.EnsureDeliveryParts(deliveryParts.Values, cancellationToken);
        Task<int> ensureErrors = partRepository.EnsureErrorParts(errorDays, cancellationToken);
        await Task.WhenAll(ensureDelivery, ensureErrors).ConfigureAwait(false);

        IReadOnlyDictionary<Exception, ErrorInfo> errors = errorLogs is not { Count: > 0 }
            ? ReadOnlyDictionary<Exception, ErrorInfo>.Empty
            : await errorCmd.Execute(errorLogs.ToArray(), cancellationToken).ConfigureAwait(false);

        return await finishCmd.Execute(messages, errors, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands every permanent failure to <see cref="IPgOutboxPoisonHandler"/> before the batch is
    /// finalized and returns only the contexts whose handler chose
    /// <see cref="PgOutboxPoisonAction.Default"/> — those still need the <c>__error$</c> row.
    /// </summary>
    /// <remarks>
    /// The payload is re-serialized with the provider serializer, so the handler sees the same bytes
    /// the message was published with and does not need to know <typeparamref name="TMessage"/>.
    /// </remarks>
    private async Task<List<IOutboxContextOperations<TMessage>>?> InterceptPoisonHandlersAsync<TMessage>(
        List<IOutboxContextOperations<TMessage>> errs,
        CancellationToken cancellationToken)
    {
        List<IOutboxContextOperations<TMessage>>? persist = null;

        foreach (IOutboxContextOperations<TMessage> context in errs)
        {
            ReadOnlyMemory<byte> payload;

            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, context.Payload);
                payload = stream.ToArray();
            }

            var message = new PgOutboxPoisonMessage(
                OutboxId: context.OutboxId,
                TaskId: context.DeliveryInfo.TaskId,
                PayloadId: context.PayloadId,
                PayloadType: typeof(TMessage).FullName ?? typeof(TMessage).Name,
                ConsumerGroup: context.DeliveryInfo.PartInfo.Part,
                TenantId: context.PartInfo.TenantId,
                Part: context.PartInfo.Part,
                Attempt: context.DeliveryInfo.Attempt + 1,
                StatusCode: context.DeliveryResult.Code,
                StatusMessage: context.DeliveryResult.Message,
                Exception: context.Exception!,
                Payload: payload);

            PgOutboxPoisonAction action;

            try
            {
                action = await poisonHandler.Handle(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPoisonHandlerFailed(_logger, message.PayloadType, message.PayloadId, ex);

                // Do not finalize the task: it must remain claimable so the handler is retried.
                throw;
            }

            switch (action)
            {
                case PgOutboxPoisonAction.Default:
                    (persist ??= []).Add(context);
                    break;

                case PgOutboxPoisonAction.Handled:
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(action), action, "Unknown PostgreSQL Outbox poison action.");
            }
        }

        return persist;
    }

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => extendCmd.Execute(lockExpiration, filter, cancellationToken);

    private async Task EnsureParts(OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        // Message and task table partitions are independent DDL on different tables — run the two
        // ensures concurrently instead of awaiting them in sequence (M6).
        Task<int> msgParts = partRepository.EnsureMsgParts(
            [new OutboxPartInfo(filter.TenantId, filter.Part, filter.NowDate)],
            cancellationToken);
        Task<int> taskParts = partRepository.EnsureTaskParts(
            [new OutboxPartInfo(filter.TenantId, filter.ConsumerGroupId, filter.NowDate)],
            cancellationToken);

        await Task.WhenAll(msgParts, taskParts).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 5201,
        Level = LogLevel.Error,
        Message = "PostgreSQL Outbox poison handler failed for {PayloadType}/{PayloadId}; the task is left Processing and will be re-rented.")]
    static partial void LogPoisonHandlerFailed(ILogger logger, string payloadType, string payloadId, Exception exception);
}
