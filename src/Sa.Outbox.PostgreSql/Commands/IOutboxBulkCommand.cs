namespace Sa.Outbox.PostgreSql.Commands;

internal interface IOutboxBulkCommand
{
    ValueTask<ulong> BulkWrite<TMessage>(ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken);
}
