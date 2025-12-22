using Sa.Extensions;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;

internal sealed class OutboxRepository(IMsgBulkCommand bulkCmd, IOutboxPartRepository partRepository)
    : IOutboxRepository
{

    public async ValueTask<ulong> Save<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages, 
        CancellationToken cancellationToken = default)
    {
        if (messages.Length == 0) return 0;

        OutboxPartInfo[] parts = messages.Span.SelectWhere(c => c.PartInfo);
        await partRepository.EnsureMsgParts(parts, cancellationToken);

        return await bulkCmd.BulkWrite(messages, cancellationToken);
    }
}
