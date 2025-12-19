using System.Collections.ObjectModel;
using System.Data;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class DeliveryRepository(
    IStartDeliveryCommand startCmd
    , IErrorDeliveryCommand errorCmd
    , IFinishDeliveryCommand finishCmd
    , IExtendDeliveryCommand extendCmd
    , IOutboxPartRepository partRepository
    , IOutboxTaskLoader loader
) : IDeliveryRepository
{
    public async Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        var batchSize = writeBuffer.Length;

        if (cancellationToken.IsCancellationRequested || batchSize == 0) return 0;

        var _ = await loader.LoadGroupBatch(filter, batchSize, cancellationToken);

        return await startCmd.FillContext(writeBuffer, lockDuration, filter, cancellationToken);
    }

    public async Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Exception, ErrorInfo> errors = await GetErrors(outboxMessages, cancellationToken);

        var parts = outboxMessages
            .Span
            .SelectWhere(c => c.DeliveryInfo.PartInfo with { CreatedAt = c.DeliveryResult.CreatedAt })
            .Distinct();

        await partRepository.EnsureDeliveryParts(parts, cancellationToken);

        return await finishCmd.Execute(outboxMessages, errors, filter, cancellationToken);
    }

    private async ValueTask<IReadOnlyDictionary<Exception, ErrorInfo>> GetErrors<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages,
        CancellationToken cancellationToken)
    {
        IOutboxContextOperations<TMessage>[] errs = outboxMessages
            .Span
            .SelectWhere(m => m, m => m.Exception != null);

        if (errs.Length > 0)
            return ReadOnlyDictionary<Exception, ErrorInfo>.Empty;

        var dates = errs.Select(c => c.DeliveryResult.CreatedAt);

        await partRepository.EnsureErrorParts(dates, cancellationToken);

        return await errorCmd.Execute(errs, cancellationToken);

    }

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => extendCmd.Execute(lockExpiration, filter, cancellationToken);
}
