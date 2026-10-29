namespace Sa.Outbox.Publication;

/// <summary>
/// Defines a contract for publishing outbox messages.
/// </summary>
public interface IOutboxMessagePublisher
{
    /// <summary>
    /// Publishes a collection of messages, optionally enlisting the writes into a
    /// provider-specific publish context (for example, an external database transaction).
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="tenantId">The tenant the messages belong to.</param>
    /// <param name="context">
    /// An opaque provider-specific context. <c>null</c> for the default (auto-commit) behavior.
    /// Out of the box only the PostgreSQL provider interprets it
    /// (see <c>PgOutboxPublishExtensions</c>); a provider that does not support contexts throws
    /// <see cref="NotSupportedException"/> when one is supplied.
    /// </param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> with the number of successfully published messages.</returns>
    ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        object? context,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Publishes a collection of messages without a publish context (auto-commit).
    /// </summary>
    /// <typeparam name="TMessage">The type of the messages to be published.</typeparam>
    /// <param name="messages">A collection of messages to be published.</param>
    /// <param name="tenantId">The tenant the messages belong to.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A <see cref="ValueTask{ulong}"/> with the number of successfully published messages.</returns>
    ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        CancellationToken cancellationToken = default)
            => Publish(messages, tenantId, context: null, cancellationToken);


    /// <summary>
    /// Publishes messages grouped by tenant using a caller-supplied tenant ID resolver.
    /// Messages are automatically partitioned by tenant before publishing.
    /// </summary>
    async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        Func<TMessage, int> getTenantId,
        object? context,
        CancellationToken cancellationToken = default)
    {
        ulong totals = 0;
        foreach (IGrouping<int, TMessage> group in messages.ToLookup(getTenantId))
        {
            totals += await Publish<TMessage>([.. group], group.Key, context, cancellationToken);
        }

        return totals;
    }


    /// <summary>
    /// Publishes messages grouped by tenant (no publish context).
    /// </summary>
    ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        Func<TMessage, int> getTenantId,
        CancellationToken cancellationToken = default)
            => Publish(messages, getTenantId, context: null, cancellationToken);


    /// <summary>
    /// Publishes messages grouped by tenant, deriving each tenant ID from
    /// <see cref="IOutboxPublishable.GetTenantId"/>.
    /// </summary>
    async ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        object? context,
        CancellationToken cancellationToken = default)
            where TMessage : IOutboxPublishable
    {
        ulong totals = 0;
        foreach (var group in messages.ToLookup(m => m.GetTenantId()))
        {
            totals += await Publish<TMessage>([.. group], group.Key, context, cancellationToken);
        }

        return totals;
    }


    /// <summary>
    /// Publishes messages grouped by tenant, deriving each tenant ID from
    /// <see cref="IOutboxPublishable.GetTenantId"/> (no publish context).
    /// </summary>
    ValueTask<ulong> Publish<TMessage>(
        IReadOnlyCollection<TMessage> messages,
        CancellationToken cancellationToken = default)
            where TMessage : IOutboxPublishable
                => Publish(messages, context: null, cancellationToken);


    /// <summary>
    /// Publishes a single message for a specific tenant with a publish context.
    /// </summary>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId,
        object? context,
        CancellationToken cancellationToken = default)
            => Publish<TMessage>([message], tenantId, context, cancellationToken);


    /// <summary>
    /// Publishes a single message for a specific tenant.
    /// </summary>
    /// <returns>A <see cref="ValueTask{ulong}"/> with the number of successfully published messages (always 1 on success).</returns>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        int tenantId,
        CancellationToken cancellationToken = default)
            => PublishSingle(message, tenantId, context: null, cancellationToken);


    /// <summary>
    /// Publishes a single message with a publish context, deriving the tenant ID from
    /// <see cref="IOutboxPublishable.GetTenantId"/>.
    /// </summary>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        object? context,
        CancellationToken cancellationToken = default)
            where TMessage : IOutboxPublishable
                => PublishSingle(message, message.GetTenantId(), context, cancellationToken);


    /// <summary>
    /// Publishes a single message, deriving the tenant ID from <see cref="IOutboxPublishable.GetTenantId"/>.
    /// </summary>
    ValueTask<ulong> PublishSingle<TMessage>(
        TMessage message,
        CancellationToken cancellationToken = default)
            where TMessage : IOutboxPublishable
                => PublishSingle(message, message.GetTenantId(), context: null, cancellationToken);
}
