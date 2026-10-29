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
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);

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

    public ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken = default)
            => InsertBulk(messages, context: null, cancellationToken);

    public async ValueTask<ulong> InsertBulk<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
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

        if (messages.Length == 0) return 0;

        // Dedupe before the repository sees the parts. SelectWhere used to allocate an
        // OutboxPartInfo[N] array and EnsureParts then filled an N-entry HashSet even when every
        // message of the batch carried the same part — which is the normal publish shape. The
        // dictionary keeps one entry per distinct (day, tenant, part); the repository truncates
        // the day again, so copying it here is not required.
        Dictionary<(DateTimeOffset Day, int TenantId, string Part), OutboxPartInfo> parts = new();

        foreach (OutboxMessage<TMessage> message in messages.Span)
        {
            OutboxPartInfo part = message.PartInfo;
            var key = (part.CreatedAt.StartOfDay(), part.TenantId, part.Part);

            if (!parts.ContainsKey(key))
            {
                parts[key] = part;
            }
        }

        await partRepository.EnsureMsgParts(parts.Values, cancellationToken);

        return transaction is null
            ? await bulkCmd.Execute(messages, cancellationToken)
            : await bulkCmd.Execute(messages, transaction, cancellationToken);
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
