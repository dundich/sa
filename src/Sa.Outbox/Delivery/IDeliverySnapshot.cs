using Sa.Schedule;

namespace Sa.Outbox.Delivery;

public interface IDeliverySnapshot
{
    IJobSettings[] JobSettings { get; }
    ConsumerGroupSettings[] ConsumerSettings { get; }
    string[] Parts { get; }

    IEnumerable<string> GetConsumeGroupIds()
        => ConsumerSettings.Select(c => c.ConsumerGroupId).Distinct();
}
