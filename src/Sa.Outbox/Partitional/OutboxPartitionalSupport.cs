using Sa.Outbox.Delivery;

namespace Sa.Outbox.Partitional;


internal sealed class OutboxPartitionalSupport(
    IDelivarySnapshot? snapshot = null,
    ITenantProvider? tenantProvider = null) : IOutboxPartitionalSupport
{
    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetMsgParts(CancellationToken cancellationToken)
    {
        return await GetPairs(snapshot?.Parts ?? [], cancellationToken);
    }

    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetTaskParts(CancellationToken cancellationToken)
    {
        return await GetPairs(snapshot?.GetConsumeGroupIds() ?? [], cancellationToken);
    }

    public async ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        if (tenantProvider == null) return [];

        return await tenantProvider.GetTenantIds(cancellationToken);
    }

    private async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPairs(IEnumerable<string> parts, CancellationToken cancellationToken)
    {
        int[] tenantIds = await GetTenantIds(cancellationToken);
        if (tenantIds.Length == 0) return [];

        List<OutboxTenantPartPair> result = [];

        foreach (int tenantId in tenantIds)
        {
            foreach (string part in parts)
            {
                result.Add(new(tenantId, part));
            }
        }

        return result;
    }
}
