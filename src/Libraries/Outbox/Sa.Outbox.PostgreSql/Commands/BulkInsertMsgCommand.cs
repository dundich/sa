using Microsoft.IO;
using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.Services;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;
using System.Data;

namespace Sa.Outbox.PostgreSql.Commands;


internal sealed class BulkInsertMsgCommand(
    IPgDataSource dataSource
    , SqlOutboxBuilder sql
    , RecyclableMemoryStreamManager streamManager
    , IOutboxMessageSerializer serializer
    , IOutboxIdGenerator idGenerator
    , IOutboxTypeResolver hashResolver
) : IBulkInsertMsgCommand
{
    public async ValueTask<ulong> Execute<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetHashCode(typeof(TMessage).Name, cancellationToken);

        return await BulkWithRetry(batch, typeCode, cancellationToken);
    }

    public async ValueTask<ulong> Execute<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        NpgsqlConnection connection = GetOpenConnection(transaction);

        long typeCode = await hashResolver.GetHashCode(typeof(TMessage).Name, cancellationToken);

        await LockPublishParts(
            [PgAdvisoryKeys.ForPart(batch.PartInfo.TenantId, batch.PartInfo.Part)],
            connection, transaction, cancellationToken);

        await using NpgsqlBinaryImporter writer = await connection
            .BeginBinaryImportAsync(sql.SqlBulkMsgCopy, cancellationToken)
            .ConfigureAwait(false);

        WriteRows(writer, typeCode, batch);

        return await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ulong> Execute<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetHashCode(typeof(TMessage).Name, cancellationToken);

        return await BulkWithRetry(messages, typeCode, cancellationToken);
    }

    public async ValueTask<ulong> Execute<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        NpgsqlConnection connection = GetOpenConnection(transaction);

        long typeCode = await hashResolver.GetHashCode(typeof(TMessage).Name, cancellationToken);

        await LockPublishParts(DistinctPartKeys(messages), connection, transaction, cancellationToken);

        await using NpgsqlBinaryImporter writer = await connection
            .BeginBinaryImportAsync(sql.SqlBulkMsgCopy, cancellationToken)
            .ConfigureAwait(false);

        WriteRows(writer, typeCode, messages);

        return await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keys of every distinct (tenant, part) in the batch, ordered so that two publishers sharing
    /// several parts always take them in the same order (no deadlock), and de-duplicated so a batch
    /// that repeats one part locks it once.
    /// </summary>
    private static int[] DistinctPartKeys<TMessage>(ReadOnlyMemory<OutboxMessage<TMessage>> messages)
    {
        var keys = new int[messages.Length];
        int count = 0;

        foreach (OutboxMessage<TMessage> message in messages.Span)
        {
            OutboxPartInfo part = message.PartInfo;
            int key = PgAdvisoryKeys.ForPart(part.TenantId, part.Part);

            if (Array.IndexOf(keys, key, 0, count) < 0)
            {
                keys[count++] = key;
            }
        }

        Array.Sort(keys, 0, count);
        return count == keys.Length ? keys : keys[..count];
    }

    /// <summary>
    /// Takes the publish lock for every given part inside <paramref name="transaction"/>. The lock is
    /// what keeps <c>msg_seq</c> order equal to commit order within a part — see
    /// <see cref="SqlOutboxBuilder.SqlLockPublishParts"/> for why the cursor needs that.
    /// </summary>
    private async Task LockPublishParts(
        int[] keys,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (keys.Length == 0) return;

        await using var command = new NpgsqlCommand(sql.SqlLockPublishParts, connection, transaction);
        command.AddParamPublishLock(keys);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ulong> BulkWithRetry<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        long typeCode,
        CancellationToken cancellationToken)
    {
        int[] keys = [PgAdvisoryKeys.ForPart(batch.PartInfo.TenantId, batch.PartInfo.Part)];

        return await PgRetryStrategy.ExecuteWithRetry(
            async t => await InsertUnderPartLock(
                keys,
                async (writer, ct) =>
                {
                    WriteRows(writer, typeCode, batch);
                    return await writer.CompleteAsync(ct);
                },
                t)
            , cancellationToken: cancellationToken);
    }

    private async Task<ulong> BulkWithRetry<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        long typeCode,
        CancellationToken cancellationToken)
    {
        int[] keys = DistinctPartKeys(messages);

        return await PgRetryStrategy.ExecuteWithRetry(
            async t => await InsertUnderPartLock(
                keys,
                async (writer, ct) =>
                {
                    WriteRows(writer, typeCode, messages);
                    return await writer.CompleteAsync(ct);
                },
                t)
            , cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Runs the COPY in a transaction of its own that also holds the publish lock. The explicit
    /// transaction is required, not incidental: a <c>pg_advisory_xact_lock</c> taken by a separate
    /// statement would be released at the end of that statement's implicit transaction, i.e. before
    /// the COPY it is supposed to cover.
    /// </summary>
    private async Task<ulong> InsertUnderPartLock(
        int[] keys,
        Func<NpgsqlBinaryImporter, CancellationToken, Task<ulong>> write,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenDbConnection(cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await LockPublishParts(keys, connection, transaction, cancellationToken).ConfigureAwait(false);

        ulong written;

        // The importer must be disposed *before* the commit: until then the connector is still in
        // state 'Copy' and Npgsql refuses to run anything else on it (Commit included).
        await using (NpgsqlBinaryImporter writer = await connection
            .BeginBinaryImportAsync(sql.SqlBulkMsgCopy, cancellationToken)
            .ConfigureAwait(false))
        {
            written = await write(writer, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return written;
    }

    private static NpgsqlConnection GetOpenConnection(NpgsqlTransaction transaction)
    {
        NpgsqlConnection connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The supplied transaction is not associated with a connection. " +
                "The transaction may already be completed.");

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The connection of the supplied transaction is not open.");
        }

        return connection;
    }

    /// <summary>
    ///     <code>
    ///     msg_id
    ///     ,tenant_id
    ///     ,msg_part
    ///     ,msg_payload_id
    ///     ,msg_payload_type
    ///     ,msg_payload
    ///     ,msg_payload_size
    ///     ,msg_created_at
    ///     </code>
    /// </summary>
    private void WriteRows<TMessage>(
        NpgsqlBinaryImporter writer,
        long payloadTypeCode,
        OutboxMessageBatch<TMessage> batch)
    {
        // One pooled stream serves the whole batch (M2): each payload is serialized into it in
        // place — only Length rewinds, the buffer and capacity are reused — and then written as a
        // single Bytea value. The old code called streamManager.GetStream() per message, wrapping
        // every payload in a fresh stream object just to hand it to the Bytea writer.
        using RecyclableMemoryStream stream = streamManager.GetStream();

        OutboxPartInfo partInfo = batch.PartInfo;

        foreach (OutboxMessageBatchItem<TMessage> row in batch.Messages.Span)
        {
            WriteRow(writer, payloadTypeCode, row.PayloadId, row.Payload, partInfo, stream);
        }
    }

    private void WriteRows<TMessage>(
        NpgsqlBinaryImporter writer,
        long payloadTypeCode,
        ReadOnlyMemory<OutboxMessage<TMessage>> messages)
    {
        using RecyclableMemoryStream stream = streamManager.GetStream();

        foreach (OutboxMessage<TMessage> row in messages.Span)
        {
            WriteRow(writer, payloadTypeCode, row.PayloadId, row.Payload, row.PartInfo, stream);
        }
    }

    private void WriteRow<TMessage>(
        NpgsqlBinaryImporter writer,
        long payloadTypeCode,
        string payloadId,
        TMessage payload,
        OutboxPartInfo partInfo,
        RecyclableMemoryStream stream)
    {
        Guid id = idGenerator.GenId(partInfo.CreatedAt);

        writer.StartRow();

        // id
        writer.Write(id, NpgsqlDbType.Uuid);
        // tenant
        writer.Write(partInfo.TenantId, NpgsqlDbType.Integer);
        // part
        writer.Write(partInfo.Part, NpgsqlDbType.Text);
        // payload_id
        writer.Write(payloadId, NpgsqlDbType.Text);
        // payload_type
        writer.Write(payloadTypeCode, NpgsqlDbType.Bigint);
        // payload
        int streamLength = WritePayload(stream, payload);
        writer.Write(stream, NpgsqlDbType.Bytea);
        // payload_size
        writer.Write(streamLength, NpgsqlDbType.Integer);
        // created_at
        writer.Write(partInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint);
    }

    private int WritePayload<TMessage>(RecyclableMemoryStream stream, TMessage? payload)
    {
        stream.SetLength(0); // rewind in place — the pooled buffer carries over to the next payload
        serializer.Serialize(stream, payload);
        stream.Position = 0;
        return (int)stream.Length;
    }
}
