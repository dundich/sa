using Npgsql;
using Sa.Extensions;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal sealed class OutboxBulkWriter(
    IBulkInsertMsgCommand bulkCmd,
    IOutboxPartRepository partRepository,
    IPgOutboxDatabaseGuard databaseGuard) : IOutboxBulkWriter
{

    public ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken = default)
            => InsertBulk(messages, context: null, cancellationToken);

    public async ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        object? context,
        CancellationToken cancellationToken = default)
    {
        NpgsqlTransaction? transaction = context switch
        {
            null => null,
            NpgsqlTransaction tx => tx,
            _ => throw new ArgumentException(
                $"Unsupported publish context of type '{context.GetType().FullName}'. " +
                "Expected an NpgsqlTransaction.",
                nameof(context)),
        };

        // The guard runs before any partition bookkeeping so a foreign transaction fails fast
        // without creating leftover partitions in the Outbox database.
        if (transaction is not null)
        {
            databaseGuard.EnsureSameDatabase(transaction);
        }

        if (messages.Length == 0) return 0;

        OutboxPartInfo[] parts = messages.Span.SelectWhere(c => c.PartInfo);
        await partRepository.EnsureMsgParts(parts, cancellationToken);

        return transaction is null
            ? await bulkCmd.Execute(messages, cancellationToken)
            : await bulkCmd.Execute(messages, transaction, cancellationToken);
    }
}
