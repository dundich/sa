using Sa.Outbox.Job;
using Sa.Outbox.Publication;
using Sa.Schedule;

namespace Sa.Outbox.Delivery;

internal sealed class DelivarySnapshot(IScheduleSettings scheduleSettings) : IDelivarySnapshot
{

    private readonly Lazy<IJobSettings[]> _lazyJobs = new(() => [.. scheduleSettings.GetJobSettings()]);


    private readonly Lazy<string[]> _lazyParts = new(() =>
    {
        Type baseType = typeof(DeliveryJob<>);
        string[] parts = [.. scheduleSettings.GetJobSettings()
            .Select(c => GetMessageTypeIfInheritsFromDeliveryJob(c.JobType, baseType))
            .Where(mt => mt != null)
            .Cast<Type>()
            .Select(mt => OutboxMessageTypeHelper.GetOutboxMessageTypeInfo(mt).PartName)
            .Distinct()];

        return parts;
    });

    private readonly Lazy<ConsumerGroupSettings[]> _lazyDeliveries = new(() =>
    {
        ConsumerGroupSettings[] settings = [.. scheduleSettings.GetJobSettings()
            .Select(c => c.Properties.Tag as ConsumerGroupSettings)
            .Where(mt => mt != null)
            .Cast<ConsumerGroupSettings>()];

        return settings;
    });


    private static Type? GetMessageTypeIfInheritsFromDeliveryJob(Type jobType, Type baseType)
    {
        if (!baseType.IsGenericTypeDefinition) return null;

        if (jobType.IsGenericType && jobType.GetGenericTypeDefinition() == baseType)
            return jobType.GenericTypeArguments[0];

        return jobType.BaseType != null
            ? GetMessageTypeIfInheritsFromDeliveryJob(jobType.BaseType, baseType)
            : null;
    }


    public string[] Parts => _lazyParts.Value;
    public IJobSettings[] JobSettings => _lazyJobs.Value;
    public ConsumerGroupSettings[] ConsumerSettings => _lazyDeliveries.Value;
}
