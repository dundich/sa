using System.Data;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;


internal sealed class DeliveryRepository(
    IStartDeliveryCommand startCmd
    , IErrorDeliveryCommand errorCmd
    , IFinishDeliveryCommand finishCmd
    , IExtendDeliveryCommand extendCmd
    , IOutboxPartRepository partRepository
    , IConsumeLoader loader
) : IDeliveryRepository
{
    public async Task<int> RentDelivery<TMessage>(
        Memory<OutboxDeliveryMessage<TMessage>> writeBuffer,
        int batchSize,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || batchSize < 1) return 0;

        var _ = await loader.LoadGroup(filter, batchSize, cancellationToken);

        return await startCmd.Execute(writeBuffer, batchSize, lockDuration, filter, cancellationToken);
    }

    public async Task<int> ReturnDelivery(IOutboxContext[] outboxMessages, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Exception, ErrorInfo> errors = await GetErrors(outboxMessages, cancellationToken);

        IEnumerable<OutboxPartInfo> parts = outboxMessages
            .Select(c => c.DeliveryInfo.PartInfo with { CreatedAt = c.DeliveryResult.CreatedAt });

        await partRepository.EnsureDeliveryParts(parts, cancellationToken);

        return await finishCmd.Execute(outboxMessages, errors, filter, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<Exception, ErrorInfo>> GetErrors(IOutboxContext[] outboxMessages, CancellationToken cancellationToken)
    {
        IEnumerable<DateTimeOffset> errOnDates = outboxMessages
            .Where(m => m.Exception != null)
            .Select(m => m.DeliveryResult.CreatedAt);

        await partRepository.EnsureErrorParts(errOnDates, cancellationToken);

        IReadOnlyDictionary<Exception, ErrorInfo> errors = await errorCmd.Execute(outboxMessages, cancellationToken);
        return errors;
    }

    public async Task<int> ExtendDelivery(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        return await extendCmd.Execute(lockExpiration, filter, cancellationToken);
    }
}
