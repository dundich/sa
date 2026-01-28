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
        int tenantId = 0,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Publishes foreach tenants
    /// </summary>
    async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        Func<TMessage, int> getTenantId,
        CancellationToken cancellationToken = default)
    {

        var lookup = messages.ToLookup(getTenantId);
        ulong totals = 0;

        foreach (var tenantId in lookup.Select(g => g.Key))
        {
            var group = lookup[tenantId];
            var tenantMessages = group.ToArray();

            totals += await Publish<TMessage>(tenantMessages, tenantId, cancellationToken);
        }

        return totals;
    }

    /// <summary>
    /// Publishes a single message.
    /// </summary>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId = 0,
        CancellationToken cancellationToken = default)
            => Publish<TMessage>([message], tenantId, cancellationToken);
}
