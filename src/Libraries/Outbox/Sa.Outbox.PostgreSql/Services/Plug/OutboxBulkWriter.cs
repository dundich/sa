using Npgsql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.Commands;
using Sa.Outbox.PostgreSql.Services;

namespace Sa.Outbox.PostgreSql.Services.Plug;

internal sealed class OutboxBulkWriter(
    IBulkInsertMsgCommand bulkCmd,
    IOutboxPartRepository partRepository,
    IPgOutboxDatabaseGuard databaseGuard) : IOutboxBulkWriter
{
    public async ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        NpgsqlTransaction? transaction = ResolveTransaction(context);

        // The guard runs before any partition bookkeeping so a foreign transaction fails fast
        // without creating leftover partitions in the Outbox database.
        if (transaction is not null)
        {
            databaseGuard.EnsureSameDatabase(transaction);
        }

        if (batch.IsEmpty) return 0;

        // One part for the whole batch: the core publisher shares it, so there is nothing to
        // dedupe and no per-message wrapper to walk.
        await partRepository.EnsureMsgParts([batch.PartInfo], cancellationToken);

        return transaction is null
            ? await bulkCmd.Execute(batch, cancellationToken)
            : await bulkCmd.Execute(batch, transaction, cancellationToken);
    }

    private static NpgsqlTransaction? ResolveTransaction(object? context)
        => context switch
        {
            null => null,
            NpgsqlTransaction tx => tx,
            _ => throw new ArgumentException(
                $"Unsupported publish context of type '{context.GetType().FullName}'. " +
                "Expected an NpgsqlTransaction.",
                nameof(context)),
        };
}
