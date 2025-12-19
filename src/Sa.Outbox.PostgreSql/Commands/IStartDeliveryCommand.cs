namespace Sa.Outbox.PostgreSql.Commands;

internal interface IStartDeliveryCommand
{
    Task<int> FillContext<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken);
}