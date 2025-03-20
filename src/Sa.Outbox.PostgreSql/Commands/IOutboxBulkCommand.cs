namespace Sa.Outbox.PostgreSql.Commands;

internal interface IOutboxBulkCommand
{
    ValueTask<ulong> BulkWrite<TMessage>(string payloadType, ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken);
}
