using Sa.Extensions;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Partitional.PostgreSql;
using System.Globalization;

namespace Sa.Outbox.PostgreSql.Services;

internal sealed class OutboxPartRepository(
    IPartitionManager partManager,
    PgOutboxTableSettings tableSettings): IOutboxPartRepository
{
    /// <summary>What the two partition key values of a message-table leaf are, for error messages.</summary>
    private const string MessageKey = "tenant id, message part";

    /// <summary>What the two partition key values of a task/delivery leaf are, for error messages.</summary>
    private const string ConsumerGroupKey = "tenant id, consumer group";

    public Task<int> EnsureMsgParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.Message.TableName, MessageKey, outboxParts, cancellationToken);

    public Task<int> EnsureTaskParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.TaskQueue.TableName, ConsumerGroupKey, outboxParts, cancellationToken);

    public Task<int> EnsureDeliveryParts(IEnumerable<OutboxPartInfo> outboxParts, CancellationToken cancellationToken)
        => EnsureParts(tableSettings.Delivery.TableName, ConsumerGroupKey, outboxParts, cancellationToken);


    public async Task<int> EnsureErrorParts(IEnumerable<DateTimeOffset> dates, CancellationToken cancellationToken)
    {
        // Callers hand over a day-keyed set (ReturnDelivery builds it with StartOfDay), so the
        // extra Select(...).Distinct() pass only allocated an iterator and a second set.
        // StartOfDay is repeated defensively for any direct caller; it is a value-type operation.
        int i = 0;
        foreach (DateTimeOffset date in dates)
        {
            i++;
            await partManager.EnsureParts(tableSettings.Error.TableName, date.StartOfDay(), [], cancellationToken);
        }

        return i;
    }

    public Task<int> Migrate() => partManager.Migrate(CancellationToken.None);


    private async Task<int> EnsureParts(
        string databaseTableName,
        string partitionKeyNames,
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

            // A leaf is named `table__{tenant}__{key}__{date}`, and PostgreSQL truncates an identifier
            // over 63 bytes. Checked here, where the real tenant and key are known, so the message can
            // name the value that does not fit instead of leaving the reader to count bytes in the
            // generated name. The startup check in PgOutboxTableSettings covers the group ids alone,
            // because the tenant is only known at this point.
            PgOutboxPartitionNames.EnsureFits(
                databaseTableName,
                partitionKeyNames,
                [part.TenantId.ToString(CultureInfo.InvariantCulture), part.Part]);

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
