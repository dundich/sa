using Npgsql;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.PostgreSql.Commands;

internal interface IBulkInsertMsgCommand
{
    ValueTask<ulong> ExecuteAsync<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes the batch on the connection of <paramref name="transaction"/>, inside it.
    /// No automatic retry: a failure leaves the caller's transaction aborted and only the caller can
    /// decide what to do with it.
    /// </summary>
    ValueTask<ulong> ExecuteAsync<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken);
}
