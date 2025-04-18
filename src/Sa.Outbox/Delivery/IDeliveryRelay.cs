namespace Sa.Outbox.Delivery;

public interface IDeliveryRelay
{
    Task<int> StartDelivery<TMessage>(OutboxDeliverySettings settings, CancellationToken cancellationToken);
}
