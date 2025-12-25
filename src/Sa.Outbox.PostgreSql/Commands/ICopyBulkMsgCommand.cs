namespace Sa.Outbox.PostgreSql.Commands;

internal interface ICopyBulkMsgCommand
{
    ValueTask<ulong> Execute<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken);
}
