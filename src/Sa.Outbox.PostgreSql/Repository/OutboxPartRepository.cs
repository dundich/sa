using Sa.Extensions;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Repository;

internal sealed class OutboxPartRepository(IPartitionManager partManager, PgOutboxTableSettings tableSettings)
    : IOutboxPartRepository
{
    public Task<int> EnsureMsgParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.DatabaseMsgTableName, outboxParts, cancellationToken);

    public Task<int> EnsureTaskParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.DatabaseTaskTableName, outboxParts, cancellationToken);

    public Task<int> EnsureDeliveryParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.DatabaseDeliveryTableName, outboxParts, cancellationToken);


    public async Task<int> EnsureErrorParts(IEnumerable<DateTimeOffset> dates, CancellationToken cancellationToken)
    {
        int i = 0;
        foreach (DateTimeOffset date in dates.Select(c => c.StartOfDay()).Distinct())
        {
            i++;
            await partManager.EnsureParts(tableSettings.DatabaseErrorTableName, date, [], cancellationToken);
        }

        return i;
    }

    public Task<int> Migrate() => partManager.Migrate(CancellationToken.None);


    private async Task<int> EnsureParts(string databaseTableName, IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
    {
        int i = 0;
        foreach (OutboxPartInfo part in outboxParts.Distinct())
        {
            i++;
            await partManager.EnsureParts(databaseTableName, part.CreatedAt, [part.TenantId, part.Part], cancellationToken);
        }

        return i;
    }
}
