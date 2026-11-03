namespace Sa.Outbox.Delivery;

/// <summary>
/// Processes messages using a consumer in scope
/// </summary>
internal interface IDeliveryLifetimeInvoker
{
    /// <summary>
    /// Resolves a consumer from DI within an activation scope and invokes it for the given batch of messages.
    /// Ensures scoped services (e.g., DbContext) are correctly disposed after processing.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages being consumed.</typeparam>
    /// <param name="settings">Runtime delivery settings controlling consumption behavior.</param>
    /// <param name="filter">Criteria used to select which messages are eligible for consumption.</param>
    /// <param name="messages">Read-only memory containing context operations for each message.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task ConsumeInScope<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken);
}
