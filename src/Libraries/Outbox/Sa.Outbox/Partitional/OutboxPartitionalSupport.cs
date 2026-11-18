using Sa.Outbox.Delivery;

namespace Sa.Outbox.Partitional;


internal sealed class OutboxPartitionalSupport(
    IDeliverySnapshot? snapshot = null,
    ITenantProvider? tenantProvider = null,
    string providerKey = OutboxScope.DefaultKey) : IOutboxPartitionalSupport
{
    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetMsgParts(CancellationToken cancellationToken)
    {
        return await GetPairs(snapshot?.Parts ?? [], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetTaskParts(CancellationToken cancellationToken)
    {
        return await GetPairs(snapshot?.GetConsumeGroupIds() ?? [], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int[]> GetTenantIdsAsync(CancellationToken cancellationToken)
    {
        if (tenantProvider == null) return [];

        return await tenantProvider.GetTenantIdsAsync(providerKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPairs(
        IEnumerable<string> parts, CancellationToken cancellationToken)
    {
        int[] tenantIds = await GetTenantIdsAsync(cancellationToken).ConfigureAwait(false);
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
