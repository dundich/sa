using Sa.Outbox.Job;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.Partitional;

internal class OutboxPartitionalSupport(IScheduleSettings scheduleSettings, PartitionalSettings partSettings) : IOutboxPartitionalSupport
{
    private readonly Lazy<string[]> s_lazyParts = new(() =>
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
        string[] parts = s_lazyParts.Value;

        if (parts.Length == 0) return [];

        if (partSettings?.GetTenantIds == null) return [];


        int[] tenantIds = await partSettings.GetTenantIds(cancellationToken);
        if (tenantIds.Length == 0) return [];

        List<OutboxTenantPartPair> result = [];

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

        if (type.BaseType != null)
            return GetMessageTypeIfInheritsFromDeliveryJob(type.BaseType, baseType);

        return null;
    }
}
