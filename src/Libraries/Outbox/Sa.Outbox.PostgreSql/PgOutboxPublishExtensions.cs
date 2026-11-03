using Npgsql;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Explicit entry points for publishing outbox messages inside a caller-controlled PostgreSQL
/// transaction. The transaction is boxed into the opaque publish context that
/// <see cref="Sa.Outbox.Publication.IOutboxMessagePublisher"/> carries, so the write runs in
/// <paramref name="transaction"/> and only becomes visible when the caller commits it.
/// </summary>
public static class PgOutboxPublishExtensions
{
    /// <summary>
    /// Publishes messages so that the writes are executed inside <paramref name="transaction"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The transaction belongs to a different database than the Outbox data source.
    /// </exception>
    public static ValueTask<ulong> Publish<TMessage>(
        this IOutboxMessagePublisher publisher,
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(transaction);
        return publisher.Publish(messages, tenantId, (object)transaction, cancellationToken);
    }

    /// <summary>
    /// Publishes a single message inside <paramref name="transaction"/>.
    /// </summary>
    public static ValueTask<ulong> PublishSingle<TMessage>(
        this IOutboxMessagePublisher publisher,
        TMessage message,
        int tenantId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(transaction);
        return publisher.PublishSingle(message, tenantId, (object)transaction, cancellationToken);
    }
}
