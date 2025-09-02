namespace Sa.Outbox.PostgreSql.Commands;

internal interface IExtendDeliveryCommand
{
    Task<int> Execute(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
