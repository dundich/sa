using Sa.Outbox.Delivery;

namespace Sa.Outbox.Partitional;


internal sealed class OutboxPartitionalSupport(
    IDelivaryConfiguration configuration,
    PartitionalSettings? partSettings) : IOutboxPartitionalSupport
{
    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetMsgParts(CancellationToken cancellationToken)
    {
        return await GetPairs(configuration.Parts, cancellationToken);
    }

    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetTaskParts(CancellationToken cancellationToken)
    {
        return await GetPairs(configuration.GetConsumeGroupIds(), cancellationToken);
    }

    private async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPairs(IEnumerable<string> parts, CancellationToken cancellationToken)
    {
        if (partSettings?.GetTenantIds == null) return [];

        int[] tenantIds = await partSettings.GetTenantIds(cancellationToken);
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
