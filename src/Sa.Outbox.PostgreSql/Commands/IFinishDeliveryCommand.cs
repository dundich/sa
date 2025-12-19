namespace Sa.Outbox.PostgreSql.Commands;

internal interface IFinishDeliveryCommand
{
    Task<int> Execute<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> outboxMessages, 
        IReadOnlyDictionary<Exception, ErrorInfo> errors, 
        OutboxMessageFilter filter, 
        CancellationToken cancellationToken);
}
