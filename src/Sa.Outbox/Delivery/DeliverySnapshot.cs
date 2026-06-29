namespace Sa.Outbox.Delivery;

internal sealed class DeliverySnapshot : IDeliverySnapshot
{
    private readonly Lazy<OutboxConsumerSettings[]> _lazyDeliveries = new(() =>
    {
        // Collect settings from the static registry populated by AddDeliveryJob
        var registered = Setup.RegisteredSettings
            .Where(s => s != null)
            .DistinctBy(s => s.ConsumerGroupId)
            .ToArray();

        return registered;
    });

    public string[] Parts
    {
        get
        {
            var settings = _lazyDeliveries.Value;
            return [.. settings.Select(s => s.ConsumerGroupId).Distinct()];
        }
    }

    public OutboxConsumerSettings[] ConsumerSettings => _lazyDeliveries.Value;
}
