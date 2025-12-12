using Sa.Schedule;

namespace Sa.Outbox.Delivery;

public interface IDelivarySnapshot
{
    IJobSettings[] JobSettings { get; }
    OutboxDeliverySettings[] DeliverySettings { get; }
    string[] Parts { get; }

    IEnumerable<string> GetConsumeGroupIds()
        => DeliverySettings.Select(c => c.ConsumeSettings.ConsumerGroupId).Distinct();
}
