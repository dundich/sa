namespace Sa.Outbox.PostgreSql.Commands;

internal interface IFinishDeliveryCommand
{
    Task<int> ExecuteAsync<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        IReadOnlyDictionary<Exception, ErrorInfo> errors,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);
}
