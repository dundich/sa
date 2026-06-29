using Sa.Schedule;

namespace Sa.Outbox.Delivery.Job;

internal static class JobPropertiesExtension
{
    public static OutboxConsumerSettings? GetConsumerGroupSettings(this IJobProperties properties)
        => properties?.Tag as OutboxConsumerSettings;
}
