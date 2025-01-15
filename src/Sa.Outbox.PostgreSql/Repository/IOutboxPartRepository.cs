
namespace Sa.Outbox.PostgreSql.Repository;

internal interface IOutboxPartRepository
{
    Task<int> EnsureDeliveryParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken);
    Task<int> EnsureOutboxParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken);
    Task<int> EnsureErrorParts(IEnumerable<DateTimeOffset> dates, CancellationToken cancellationToken);

    Task<int> Migrate();
}
