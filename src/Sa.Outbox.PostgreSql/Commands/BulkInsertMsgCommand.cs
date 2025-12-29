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
            int streamLength = WritePayload(writer, row.Payload);
            // payload_size
            writer.Write(streamLength, NpgsqlDbType.Integer);
            // created_at
            writer.Write(row.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint);
        }
    }

    private int WritePayload<TMessage>(NpgsqlBinaryImporter writer, TMessage? payload)
    {
        using RecyclableMemoryStream stream = streamManager.GetStream();
        serializer.Serialize(stream, payload);
        stream.Position = 0;
        writer.Write(stream, NpgsqlDbType.Bytea);
        return (int)stream.Length;
    }
}
