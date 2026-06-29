namespace Sa.Outbox.Delivery;

public interface IDeliverySnapshot
{
    string[] Parts { get; }
    OutboxConsumerSettings[] ConsumerSettings { get; }
    IEnumerable<string> GetConsumeGroupIds()
        => ConsumerSettings.Select(c => c.ConsumerGroupId).Distinct();
}
