using Sa.Classes;
using Sa.Outbox.Partitional;
using Sa.Partitional.PostgreSql;

namespace Sa.Outbox.PostgreSql.Partitional;

internal class OutboxMigrationSupport(IOutboxPartitionalSupport? partitionalSupport = null) : IPartTableMigrationSupport
{
    public async Task<StrOrNum[][]> GetPartValues(CancellationToken cancellationToken)
    {
        if (partitionalSupport == null) return [];

        IReadOnlyCollection<OutboxTenantPartPair> parts = await partitionalSupport.GetPartValues(cancellationToken);

        return [.. parts.Select(c => new StrOrNum[] { c.TenantId, c.Part })];
    }
}
