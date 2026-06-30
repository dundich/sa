namespace Sa.Outbox.Publication;

/// <summary>
/// Defines a contract for publishing outbox messages.
/// </summary>
public interface IOutboxMessagePublisher
{
    /// <summary>
    /// Publishes a collection of messages.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published, which must implement <see cref="IOutboxPayloadMessage"/>.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation, 
    /// with the number of successfully published messages as the result.</returns>
    ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Publishes messages grouped by tenant using a caller-supplied tenant ID resolver.
    /// Messages are automatically partitioned by tenant before publishing.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="getTenantId">A delegate that returns the tenant ID for each message.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation,
    /// with the total number of successfully published messages across all tenants as the result.</returns>
    async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        Func<TMessage, int> getTenantId,
        CancellationToken cancellationToken = default)
    {
        ulong totals = 0;
        foreach (IGrouping<int, TMessage> group in messages.ToLookup(getTenantId))
        {
            totals += await Publish<TMessage>([.. group], group.Key, cancellationToken);
        }

        return totals;
    }


    /// <summary>
    /// Publishes messages grouped by tenant, deriving each tenant ID from <see cref="IOutboxPublishable.GetTenantId"/>.
    /// Only applicable to message types that implement <see cref="IOutboxPublishable"/>.
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published, which must implement <see cref="IOutboxPublishable"/>.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation,
    /// with the total number of successfully published messages across all tenants as the result.</returns>
    async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        CancellationToken cancellationToken = default)
            where TMessage : class, IOutboxPublishable
    {
        ulong totals = 0;
        foreach (var group in messages.ToLookup(m => m.GetTenantId()))
        {
            totals += await Publish<TMessage>([.. group], group.Key, cancellationToken);
        }

        return totals;
    }


    /// <summary>
    /// Publishes a single message for a specific tenant.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message to be published.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="tenantId">The tenant ID under which the message belongs.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation,
    /// with the number of successfully published messages (always 1 on success) as the result.</returns>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId,
        CancellationToken cancellationToken = default)
            => Publish<TMessage>([message], tenantId, cancellationToken);


    /// <summary>
    /// Publishes a single message, deriving the tenant ID from <see cref="IOutboxPublishable.GetTenantId"/>.
    /// Only applicable to message types that implement <see cref="IOutboxPublishable"/>.
    /// </summary>
    /// <typeparam name="TMessage">A message type implementing <see cref="IOutboxPublishable"/>.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> representing the asynchronous operation,
    /// with the number of successfully published messages (always 1 on success) as the result.</returns>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default)
            where TMessage : class, IOutboxPublishable
                => Publish<TMessage>([message], message.GetTenantId(), cancellationToken);
}
