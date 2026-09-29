using Microsoft.IO;
using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;

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
        ReadOnlyMemory<OutboxMessage<TMessage>> messages,
        CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetHashCode(typeof(TMessage).Name, cancellationToken);

        return await BulkWithRetry(messages, typeCode, cancellationToken);
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
        ReadOnlyMemory<OutboxMessage<TMessage>> messages)
    {
        // One pooled stream serves the whole batch (M2): each payload is serialized into it in
        // place — only Length rewinds, the buffer and capacity are reused — and then written as a
        // single Bytea value. The old code called streamManager.GetStream() per message, wrapping
        // every payload in a fresh stream object just to hand it to the Bytea writer.
        using RecyclableMemoryStream stream = streamManager.GetStream();
        foreach (OutboxMessage<TMessage> row in messages.Span)
        {
            Guid id = idGenerator.GenId(row.PartInfo.CreatedAt);

            writer.StartRow();

            // id
            writer.Write(id, NpgsqlDbType.Uuid);
            // tenant
            writer.Write(row.PartInfo.TenantId, NpgsqlDbType.Integer);
            // part
            writer.Write(row.PartInfo.Part, NpgsqlDbType.Text);
            // payload_id
            writer.Write(row.PayloadId, NpgsqlDbType.Text);
            // payload_type
            writer.Write(payloadTypeCode, NpgsqlDbType.Bigint);
            // payload
            int streamLength = WritePayload(stream, row.Payload);
            writer.Write(stream, NpgsqlDbType.Bytea);
            // payload_size
            writer.Write(streamLength, NpgsqlDbType.Integer);
            // created_at
            writer.Write(row.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint);
        }
    }

    private int WritePayload<TMessage>(RecyclableMemoryStream stream, TMessage? payload)
    {
        stream.SetLength(0); // rewind in place — the pooled buffer carries over to the next payload
        serializer.Serialize(stream, payload);
        stream.Position = 0;
        return (int)stream.Length;
    }
}
