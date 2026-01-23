namespace Sa.Outbox.PostgreSql.Commands;

internal interface IBulkInsertMsgCommand
{
    ValueTask<ulong> Execute<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken);
}
