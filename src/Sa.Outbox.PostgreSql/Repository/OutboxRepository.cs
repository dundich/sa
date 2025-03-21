using Sa.Extensions;
using Sa.Outbox.PostgreSql.Commands;

namespace Sa.Outbox.PostgreSql.Repository;

internal class OutboxRepository(IOutboxBulkCommand bulkCmd, IOutboxPartRepository partRepository)
    : IOutboxRepository
{

    public async ValueTask<ulong> Save<TMessage>(string payloadType, ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Length == 0) return 0;

        OutboxPartInfo[] parts = messages.Span.SelectWhere(c => c.PartInfo);
        await partRepository.EnsureOutboxParts(parts, cancellationToken);

        return await bulkCmd.BulkWrite(payloadType, messages, cancellationToken);
    }
}
