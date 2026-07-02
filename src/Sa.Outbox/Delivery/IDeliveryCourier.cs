namespace Sa.Outbox.Delivery;

/// <summary>
/// Delivers a batch of messages with error handling and retry mechanisms.
/// </summary>
internal interface IDeliveryCourier
{
    /// <summary>
    /// Delivers the given messages to their respective consumers, invoking the consumer for each message
    /// and applying the configured retry and backoff strategies on failure.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages being delivered.</typeparam>
    /// <param name="settings">Runtime delivery settings controlling concurrency, batching, retries, and locking.</param>
    /// <param name="filter">Criteria used to select which messages are eligible for delivery.</param>
    /// <param name="messages">Read-only memory containing context operations for each message.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The number of messages successfully delivered.</returns>
    ValueTask<int> Deliver<TMessage>(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        CancellationToken cancellationToken);
}
