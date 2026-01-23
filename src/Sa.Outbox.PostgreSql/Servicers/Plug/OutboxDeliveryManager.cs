using System.Collections.ObjectModel;
using System.Data;
using Sa.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;

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

        return await startCmd.ExecuteFill(writeBuffer, lockDuration, filter, cancellationToken);
    }

    private async Task EnsureParts(OutboxMessageFilter filter, CancellationToken cancellationToken)
    {
        await partRepository.EnsureMsgParts(
            [new OutboxPartInfo(filter.TenantId, filter.Part, filter.NowDate)],
            cancellationToken);

        await partRepository.EnsureTaskParts(
            [new OutboxPartInfo(filter.TenantId, filter.ConsumerGroupId, filter.NowDate)],
            cancellationToken);
    }

    public async Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Exception, ErrorInfo> errors = await GetErrors(messages, cancellationToken);

        var parts = messages
            .Span
            .SelectWhere(c => c.DeliveryInfo.PartInfo with { CreatedAt = c.DeliveryResult.CreatedAt })
            .Distinct();

        await partRepository.EnsureDeliveryParts(parts, cancellationToken);

        return await finishCmd.Execute(messages, errors, filter, cancellationToken);
    }

    private async ValueTask<IReadOnlyDictionary<Exception, ErrorInfo>> GetErrors<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        IOutboxContextOperations<TMessage>[] errs = messages
            .Span
            .SelectWhere(m => m, m => m.Exception != null);

        if (errs.Length == 0)
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
