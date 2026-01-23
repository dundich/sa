using Sa.Outbox.Partitional;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Outbox.PostgreSql.Partitional;

internal sealed class TaskMigrationSupport(IOutboxPartitionalSupport? partitionalSupport = null) : IPartTableMigrationSupport
{
    public async Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken)
    {
        if (partitionalSupport == null) return [];

        IReadOnlyCollection<OutboxTenantPartPair> parts = await partitionalSupport.GetTaskParts(cancellationToken);

        return [.. parts.Select(c => new StrOrNum[] { c.TenantId, c.Part })];
    }
}
