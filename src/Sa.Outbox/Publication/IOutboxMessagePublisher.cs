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
    /// Publishes foreach tenants
    /// </summary>
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
    /// Publishes a single message.
    /// </summary>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId,
        CancellationToken cancellationToken = default)
            => Publish<TMessage>([message], tenantId, cancellationToken);


    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default)
            where TMessage : class, IOutboxPublishable
                => Publish<TMessage>([message], message.GetTenantId(), cancellationToken);
}
