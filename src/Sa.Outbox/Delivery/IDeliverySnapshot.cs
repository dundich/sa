namespace Sa.Outbox.Delivery;

public interface IDeliverySnapshot
{
    OutboxConsumerSettings[] ConsumerSettings { get; }
    string[] Parts { get; }

    IEnumerable<string> GetConsumeGroupIds()
        => ConsumerSettings.Select(c => c.ConsumerGroupId).Distinct();
}
