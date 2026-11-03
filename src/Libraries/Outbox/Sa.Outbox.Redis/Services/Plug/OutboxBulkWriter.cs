using Sa.Outbox.PlugServices;
using Sa.Outbox.Redis.Commands;

namespace Sa.Outbox.Redis.Services.Plug;

/// <summary>
/// The Redis implementation of <see cref="IOutboxBulkWriter"/>: appends the batch to its stream.
/// </summary>
/// <remarks>
/// A publish context is not supported. The Redis counterpart of "enlist in the caller's
/// transaction" would be a caller-owned <c>ITransaction</c>, and this provider deliberately does
/// not implement one. Rather than accept a context and silently ignore it — the one thing a caller
/// passing a transaction must never see — the writer refuses it.
/// </remarks>
internal sealed class OutboxBulkWriter(PublishMessagesCommand publishCommand) : IOutboxBulkWriter
{
    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);

    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        EnsureNoContext(context);

        return batch.IsEmpty
            ? ValueTask.FromResult(0UL)
            : publishCommand.ExecuteAsync(batch, cancellationToken);
    }

    private static void EnsureNoContext(object? context)
    {
        if (context is not null)
        {
            throw new NotSupportedException(
                $"The Redis Outbox bulk writer does not support publish contexts (got '{context.GetType().FullName}'). " +
                "The Redis provider does not implement an enlistable transaction.");
        }
    }
}
