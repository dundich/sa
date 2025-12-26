using Sa.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;

internal sealed class OutboxBulkWriter(
    IBulkInsertMsgCommand bulkCmd, 
    IOutboxPartRepository partRepository): IOutboxBulkWriter
{

    public async ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages, 
        CancellationToken cancellationToken = default)
    {
        if (messages.Length == 0) return 0;

        OutboxPartInfo[] parts = messages.Span.SelectWhere(c => c.PartInfo);
        await partRepository.EnsureMsgParts(parts, cancellationToken);

        return await bulkCmd.Execute(messages, cancellationToken);
    }
}
