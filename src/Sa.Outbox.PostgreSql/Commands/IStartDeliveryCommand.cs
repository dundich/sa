using System.Diagnostics.CodeAnalysis;

namespace Sa.Outbox.PostgreSql.Commands;

internal interface IStartDeliveryCommand
{
    Task<int> Execute<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> writeBuffer, int batchSize, TimeSpan lockDuration, OutboxMessageFilter filter, CancellationToken cancellationToken);
}