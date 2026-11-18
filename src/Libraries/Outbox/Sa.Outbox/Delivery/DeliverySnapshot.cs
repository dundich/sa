using Sa.Outbox.Delivery.Job;
using Sa.Outbox.Metadata;
using Sa.Schedule;

namespace Sa.Outbox.Delivery;

internal sealed class DeliverySnapshot(
    IScheduleSettings scheduleSettings,
    IOutboxConsumerManager consumerManager,
    IOutboxMessageMetadataProvider metadataProvider,
    string providerKey) : IDeliverySnapshot
{

    private readonly Lazy<IJobSettings[]> _lazyJobs = new(() => [.. scheduleSettings.GetJobSettings()]);


    private readonly Lazy<string[]> _lazyParts = new(() =>
    {
        Type baseType = typeof(DeliveryJob<>);
        string[] parts = [.. scheduleSettings.GetJobSettings()
            .Select(c => GetMessageTypeIfInheritsFromDeliveryJob(c.JobType, baseType))
            .Where(mt => mt != null)
            .Cast<Type>()
            .Select(mt => metadataProvider.GetMetadata(mt).PartName)
            .Distinct()];

        return parts;
    });

    private readonly Lazy<OutboxConsumerSettings[]> _lazyDeliveries = new(() =>
    {
        // Only this provider's groups: the manager is container-wide, and a group id may repeat
        // across providers, so the composite key is the only unambiguous lookup. Enumerating all
        // ids and keeping the ones this provider owns also excludes the other providers' groups from
        // partition creation.
        OutboxConsumerSettings[] settings = [.. consumerManager
                .GetAllConsumerGroupIds()
                .Select(groupId => consumerManager.GetSettings(new OutboxGroupKey(providerKey, groupId)))
                .Where(s => s != null)
                .Cast<OutboxConsumerSettings>()];
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
    public OutboxConsumerSettings[] ConsumerSettings => _lazyDeliveries.Value;
}
