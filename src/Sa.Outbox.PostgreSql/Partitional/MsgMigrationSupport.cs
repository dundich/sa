using Sa.Outbox.Partitional;
using Sa.Partitional.PostgreSql;
using Sa.Partitional.PostgreSql.Classes;

namespace Sa.Outbox.PostgreSql.Partitional;

internal sealed class MsgMigrationSupport(IOutboxPartitionalSupport? partitionalSupport = null) : IPartTableMigrationSupport
{
    public async Task<StrOrNum[][]> GetParts(CancellationToken cancellationToken)
    {
        if (partitionalSupport == null) return [];

        IReadOnlyCollection<OutboxTenantPartPair> parts = await partitionalSupport.GetMsgParts(cancellationToken);

        return [.. parts.Select(c => new StrOrNum[] { c.TenantId, c.Part })];
    }
}
