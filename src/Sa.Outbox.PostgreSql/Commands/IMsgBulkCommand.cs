namespace Sa.Outbox.PostgreSql.Commands;

internal interface IMsgBulkCommand
{
    ValueTask<ulong> BulkWrite<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken);
}
