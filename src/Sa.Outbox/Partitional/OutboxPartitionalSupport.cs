using Sa.Outbox.Job;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.Partitional;

internal class OutboxPartitionalSupport(IScheduleSettings scheduleSettings, PartitionalSettings partSettings) : IOutboxPartitionalSupport
{
    private readonly Lazy<string[]> _lazyParts = new(() =>
    {
        Type baseType = typeof(DeliveryJob<>);

        IEnumerable<IJobSettings> jobSettings = scheduleSettings.GetJobSettings();

        string[] parts = [.. jobSettings
            .Select(c => GetMessageTypeIfInheritsFromDeliveryJob(c.JobType, baseType))
            .Where(mt => mt != null)
            .Cast<Type>()
            .Select(mt => OutboxMessageTypeHelper.GetOutboxMessageTypeInfo(mt).PartName)
            .Distinct()];

        return parts;
    });

    public async Task<IReadOnlyCollection<OutboxTenantPartPair>> GetPartValues(CancellationToken cancellationToken)
    {
        string[] parts = _lazyParts.Value;

        if (parts.Length == 0 || partSettings?.GetTenantIds == null) return [];

        int[] tenantIds = await partSettings.GetTenantIds(cancellationToken);
        if (tenantIds.Length == 0) return [];

        return GenerateOutboxTenantPartPairs(tenantIds, parts);
    }

    private static IReadOnlyCollection<OutboxTenantPartPair> GenerateOutboxTenantPartPairs(int[] tenantIds, string[] parts)
    {
        var result = new List<OutboxTenantPartPair>();

        foreach (int tenantId in tenantIds)
        {
            foreach (string part in parts)
            {
                result.Add(new OutboxTenantPartPair(tenantId, part));
            }
        }

        return result;
    }


    private static Type? GetMessageTypeIfInheritsFromDeliveryJob(Type type, Type baseType)
    {
        if (!baseType.IsGenericTypeDefinition) return null;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == baseType)
            return type.GenericTypeArguments[0];

        return type.BaseType != null
            ? GetMessageTypeIfInheritsFromDeliveryJob(type.BaseType, baseType)
            : null;
    }
}
