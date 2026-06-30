namespace Sa.Outbox.Delivery;

/// <summary>
/// Creates <see cref="IOutboxContextOperations{TMessage}"/> instances for delivering outbox messages.
/// Operations returned by this factory are used by consumers to acknowledge, error, or postpone individual messages.
/// </summary>
public interface IOutboxContextFactory
{
    /// <summary>
    /// Creates a new set of context operations scoped to the given delivery message.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message being delivered.</typeparam>
    /// <param name="deliveryMessage">The delivery message containing outbox and message metadata.</param>
    /// <returns>A new <see cref="IOutboxContextOperations{TMessage}"/> instance for acknowledging or rejecting messages.</returns>
    IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage);
}
