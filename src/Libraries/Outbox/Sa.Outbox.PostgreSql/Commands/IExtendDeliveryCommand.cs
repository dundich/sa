namespace Sa.Outbox.PostgreSql.Commands;

internal interface IExtendDeliveryCommand
{
    Task<int> ExecuteAsync(TimeSpan lockExpiration, OutboxMessageFilter filter, CancellationToken cancellationToken);
}
