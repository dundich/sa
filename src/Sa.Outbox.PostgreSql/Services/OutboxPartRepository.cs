using Sa.Extensions;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Services;

internal sealed class OutboxPartRepository(
    IPartitionManager partManager,
    PgOutboxTableSettings tableSettings): IOutboxPartRepository
{
    public Task<int> EnsureMsgParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.Message.TableName, outboxParts, cancellationToken);

    public Task<int> EnsureTaskParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.TaskQueue.TableName, outboxParts, cancellationToken);

    public Task<int> EnsureDeliveryParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.Delivery.TableName, outboxParts, cancellationToken);


    public async Task<int> EnsureErrorParts(IEnumerable<DateTimeOffset> dates, CancellationToken cancellationToken)
    {
        int i = 0;
        foreach (DateTimeOffset date in dates.Select(c => c.StartOfDay()).Distinct())
        {
            i++;
            await partManager.EnsureParts(tableSettings.Error.TableName, date, [], cancellationToken);
        }

        return i;
    }

    public Task<int> Migrate() => partManager.Migrate(CancellationToken.None);


    private async Task<int> EnsureParts(
        string databaseTableName,
        IEnumerable<OutboxPartInfo> outboxParts,
        CancellationToken cancellationToken)
    {
        // Partitions are daily, so the dedup key is (day, tenant, part) — the exact timestamp on
        // OutboxPartInfo is noise that used to fan one batch into multiple EnsureParts round trips
        // (and the old Distinct() compared it as part of the record). The day passed to the part
        // manager is StartOfDay-truncated, matching what the error path already did (M6).
        int i = 0;
        var seen = new HashSet<(DateTimeOffset Day, int TenantId, string Part)>();

        foreach (OutboxPartInfo part in outboxParts)
        {
            var day = part.CreatedAt.StartOfDay();
            if (!seen.Add((day, part.TenantId, part.Part))) continue;

            i++;
            await partManager.EnsureParts(
                databaseTableName,
                day,
                [part.TenantId, part.Part],
                cancellationToken);
        }

        return i;
    }
}
