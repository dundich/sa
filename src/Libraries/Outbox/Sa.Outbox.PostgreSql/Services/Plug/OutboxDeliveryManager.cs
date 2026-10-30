using Sa.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using System.Collections.ObjectModel;

namespace Sa.Outbox.PostgreSql.Services.Plug;


internal sealed class OutboxDeliveryManager(
    IStartDeliveryCommand startCmd
    , IErrorDeliveryCommand errorCmd
    , IFinishDeliveryCommand finishCmd
    , IExtendDeliveryCommand extendCmd
    , IOutboxPartRepository partRepository
    , IOutboxTaskLoader loader
) : IOutboxDeliveryManager
{
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
        // One pass over the batch (M7): error contexts, delivery-part keys and error day keys are
        // all derived here, each deduplicated as it goes. Delivery parts are keyed by day (M6):
        // the partition is daily, so the exact timestamp is noise that used to fan one batch into
        // several EnsureParts round trips.
        //
        // The parts are kept in a dictionary keyed by the same (day, tenant, part) tuple the
        // repository dedupes by, so the record is copied once per *distinct* part instead of once
        // per message. The old HashSet.Add(partInfo with {...}) allocated a new OutboxPartInfo for
        // every delivered message, which is the common case: one part repeated across the batch.
        List<IOutboxContextOperations<TMessage>>? errs = null;
        Dictionary<(DateTimeOffset Day, int TenantId, string Part), OutboxPartInfo> deliveryParts = new();

        // No pre-sized sets: distinct parts and error days are few even for a large batch, while
        // messages.Length entries would be allocated for every call, empty or not.
        HashSet<DateTimeOffset> errorDays = [];

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
                errorDays.Add(day);
            }
        }

        // Delivery-log and error-log partitions are independent DDL on different tables — run the
        // ensures concurrently instead of awaiting them in sequence (M6).
        Task<int> ensureDelivery = partRepository.EnsureDeliveryParts(deliveryParts.Values, cancellationToken);
        Task<int> ensureErrors = partRepository.EnsureErrorParts(errorDays, cancellationToken);
        await Task.WhenAll(ensureDelivery, ensureErrors).ConfigureAwait(false);

        IReadOnlyDictionary<Exception, ErrorInfo> errors = errs is null
            ? ReadOnlyDictionary<Exception, ErrorInfo>.Empty
            : await errorCmd.Execute(errs.ToArray(), cancellationToken).ConfigureAwait(false);

        return await finishCmd.Execute(messages, errors, filter, cancellationToken).ConfigureAwait(false);
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
}
