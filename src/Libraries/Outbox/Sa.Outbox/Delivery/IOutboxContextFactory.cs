namespace Sa.Outbox.Delivery;

/// <summary>
/// Creates <see cref="IOutboxContextOperations{TMessage}"/> instances for delivering outbox messages.
/// Operations returned by this factory are used by consumers to acknowledge, error, or postpone individual messages.
/// </summary>
public interface IOutboxContextFactory
{
    /// <summary>
    /// Creates (or rents from a pool) a set of context operations scoped to the given delivery
    /// message. The returned instance is reset for this delivery.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message being delivered.</typeparam>
    /// <param name="deliveryMessage">The delivery message containing outbox and message metadata.</param>
    /// <returns>An <see cref="IOutboxContextOperations{TMessage}"/> instance for acknowledging or rejecting messages.</returns>
    IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage);

    /// <summary>
    /// Returns a context created by this factory once its delivery cycle has finished.
    /// </summary>
    /// <remarks>
    /// The delivery pipeline calls this after the statuses have been persisted. Contexts may then
    /// be recycled, so a consumer must not retain an <see cref="IOutboxContextOperations{TMessage}"/>
    /// (or its <see cref="IOutboxContext.PartInfo"/> view) after <c>Consume</c> returns.
    /// The default implementation is a no-op, so a factory that does not pool remains correct.
    /// </remarks>
    void Return<TMessage>(IOutboxContextOperations<TMessage> context)
    {
    }
}
