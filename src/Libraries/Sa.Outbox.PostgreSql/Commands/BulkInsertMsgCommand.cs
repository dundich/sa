using Microsoft.IO;
using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Outbox.PlugServices;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Serialization;
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

        await using NpgsqlBinaryImporter writer = await connection
            .BeginBinaryImportAsync(sql.SqlBulkMsgCopy, cancellationToken)
            .ConfigureAwait(false);

        WriteRows(writer, typeCode, messages);

        return await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ulong> BulkWithRetry<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        long typeCode,
        CancellationToken cancellationToken)
    {
        return await PgRetryStrategy.ExecuteWithRetry(
            async t =>
            {
                return await dataSource.BeginBinaryImport(sql.SqlBulkMsgCopy, async (writer, t) =>
                {
                    WriteRows(writer, typeCode, batch);

                    return await writer.CompleteAsync(t);

                }, cancellationToken);
            }
            , cancellationToken: cancellationToken);
    }

    private async Task<ulong> BulkWithRetry<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        long typeCode,
        CancellationToken cancellationToken)
    {
        return await PgRetryStrategy.ExecuteWithRetry(
            async t =>
            {
                return await dataSource.BeginBinaryImport(sql.SqlBulkMsgCopy, async (writer, t) =>
                {
                    WriteRows(writer, typeCode, messages);

                    return await writer.CompleteAsync(t);

                }, cancellationToken);
            }
            , cancellationToken: cancellationToken);
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
