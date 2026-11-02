namespace Sa.Outbox.PlugServices;

/// <summary>
/// Storage-side bulk writer for outbox messages.
/// </summary>
/// <remarks>
/// <para>
/// One abstract method: the publish batch carries one shared <see cref="OutboxMessageBatch{TMessage}.PartInfo"/>
/// and no per-message wrapper, so it is the only shape a provider has to implement. The earlier
/// per-message <c>OutboxMessage&lt;TMessage&gt;</c> overloads were removed in 0.18 — they made the
/// simple implementation the slow one (an allocating adapter materialized the old shape) and forced
/// providers to support two contracts at once.
/// </para>
/// <para>
/// <c>context</c> is provider-specific and opaque: the PostgreSQL provider accepts its
/// own transaction so the insert joins the caller's transaction, a provider that cannot do that
/// rejects a non-null context. The core checks the provider's
/// <see cref="OutboxProviderCapabilities.PublishContext"/> declaration in
/// <c>IOutboxMessagePublisher</c> before calling, so the failure is a clear message with the provider
/// name rather than an implementation detail.
/// </para>
/// </remarks>
public interface IOutboxBulkWriter
{
    /// <summary>
    /// Inserts a publish batch whose messages share one
    /// <see cref="OutboxMessageBatch{TMessage}.PartInfo"/>, optionally enlisting the writes into a
    /// provider-specific <paramref name="context"/>.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message being saved.</typeparam>
    /// <param name="batch">The batch to insert.</param>
    /// <param name="context">
    /// An opaque provider-specific publish context (for example, an external database transaction),
    /// or <see langword="null"/> for the provider's default (auto-commit) behavior.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of messages saved.</returns>
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a publish batch without a publish context (auto-commit).
    /// </summary>
    /// <typeparam name="TMessage">The type of the message being saved.</typeparam>
    /// <param name="batch">The batch to insert.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of messages saved.</returns>
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);
}
