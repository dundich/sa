namespace Sa.Outbox.Delivery;

/// <summary>
/// Represents a delivery message in the Outbox with its associated payload, part information, and delivery details.
/// </summary>
/// <param name="DeliveryInfo">Gets the unique identifier for the Outbox delivery.</param>
/// <param name="Message">Message in the Outbox.</param>
/// <param name="OutboxId">Gets information about the delivery of the Outbox message.</param>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public sealed record OutboxDeliveryMessage<TMessage>(
    Guid OutboxId,
    OutboxMessage<TMessage> Message,
    OutboxTaskDeliveryInfo DeliveryInfo
);
