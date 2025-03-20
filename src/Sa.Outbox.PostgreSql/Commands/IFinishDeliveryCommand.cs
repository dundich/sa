namespace Sa.Outbox.PostgreSql.Commands;

internal interface IFinishDeliveryCommand
{
    Task<int> Execute<TMessage>(IOutboxContext<TMessage>[] outboxMessages, IReadOnlyDictionary<Exception, ErrorInfo> errors, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
