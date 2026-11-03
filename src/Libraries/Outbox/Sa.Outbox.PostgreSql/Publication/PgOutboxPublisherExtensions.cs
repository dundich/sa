using Npgsql;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSql.Publication;

/// <summary>
/// Type-safe publish helpers for the PostgreSQL provider: the caller's transaction is a typed
/// parameter instead of an <c>object?</c> context, so a wrong context cannot compile.
/// </summary>
/// <remarks>
/// <para>
/// The core <see cref="IOutboxMessagePublisher.Publish{TMessage}(IReadOnlyCollection{TMessage}, int, object?, CancellationToken)"/>
/// takes an opaque <c>object?</c> context because the core cannot reference a provider package. A
/// same-named extension would be shadowed by that instance method, which is why these helpers are
/// named <c>PublishInTransaction</c>/<c>PublishSingleInTransaction</c>.
/// </para>
/// <para>
/// Only <see cref="NpgsqlTransaction"/> is accepted. A <see cref="Sa.Data.PostgreSql.PgTransaction"/>
/// is passed as its <c>Transaction</c> property — a second overload for the wrapper would duplicate
/// the whole matrix for no gain.
/// </para>
/// <para>
/// The provider's same-database guard still applies: a transaction opened on a different database
/// fails before any partition bookkeeping.
/// </para>
/// </remarks>
public static class PgOutboxPublisherExtensions
{
    /// <summary>Publishes messages for one tenant inside <paramref name="transaction"/>.</summary>
    public static ValueTask<ulong> PublishInTransaction<TMessage>(
        this IOutboxMessagePublisher publisher,
        IReadOnlyCollection<TMessage> messages,
        int tenantId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return publisher.Publish(messages, tenantId, context: transaction, cancellationToken);
    }

    /// <summary>Publishes messages grouped by tenant inside <paramref name="transaction"/>.</summary>
    public static ValueTask<ulong> PublishInTransaction<TMessage>(
        this IOutboxMessagePublisher publisher,
        IReadOnlyCollection<TMessage> messages,
        Func<TMessage, int> getTenantId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return publisher.Publish(messages, getTenantId, context: transaction, cancellationToken);
    }

    /// <summary>
    /// Publishes messages inside <paramref name="transaction"/>, deriving each tenant from
    /// <see cref="IOutboxPublishable.GetTenantId"/>.
    /// </summary>
    public static ValueTask<ulong> PublishInTransaction<TMessage>(
        this IOutboxMessagePublisher publisher,
        IReadOnlyCollection<TMessage> messages,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
        where TMessage : IOutboxPublishable
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return publisher.Publish(messages, context: transaction, cancellationToken);
    }

    /// <summary>Publishes a single message for one tenant inside <paramref name="transaction"/>.</summary>
    public static ValueTask<ulong> PublishSingleInTransaction<TMessage>(
        this IOutboxMessagePublisher publisher,
        TMessage message,
        int tenantId,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return publisher.PublishSingle(message, tenantId, context: transaction, cancellationToken);
    }

    /// <summary>
    /// Publishes a single message inside <paramref name="transaction"/>, deriving the tenant from
    /// <see cref="IOutboxPublishable.GetTenantId"/>.
    /// </summary>
    public static ValueTask<ulong> PublishSingleInTransaction<TMessage>(
        this IOutboxMessagePublisher publisher,
        TMessage message,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken = default)
        where TMessage : IOutboxPublishable
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return publisher.PublishSingle(message, context: transaction, cancellationToken);
    }
}
