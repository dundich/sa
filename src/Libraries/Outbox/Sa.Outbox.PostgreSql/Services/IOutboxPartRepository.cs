
namespace Sa.Outbox.PostgreSql.Services;

internal interface IOutboxPartRepository
{
    Task<int> EnsureMsgPartsAsync(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken);
    Task<int> EnsureTaskPartsAsync(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken);
    Task<int> EnsureDeliveryPartsAsync(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken);
    Task<int> EnsureErrorPartsAsync(IEnumerable<DateTimeOffset> dates, CancellationToken cancellationToken);

    Task<int> MigrateAsync();
}
