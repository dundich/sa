using Microsoft.IO;
using Npgsql;
using NpgsqlTypes;
using Sa.Classes;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeHashResolve;

namespace Sa.Outbox.PostgreSql.Commands;


internal sealed class MsgBulkCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate sqlTemplate
    , RecyclableMemoryStreamManager streamManager
    , IOutboxMessageSerializer serializer
    , IIdGenerator idGenerator
    , IMsgTypeHashResolver hashResolver
) : IMsgBulkCommand
{

    public async ValueTask<ulong> BulkWrite<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages, 
        CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetCode(typeof(TMessage).Name, cancellationToken);

        return await BulkWithRetry(messages, typeCode, cancellationToken);
    }

    private async Task<ulong> BulkWithRetry<TMessage>(
        ReadOnlyMemory<OutboxMessage<TMessage>> messages, 
        long typeCode, 
        CancellationToken cancellationToken)
    {
        return await Retry.Jitter(
            async t =>
            {
                return await dataSource.BeginBinaryImport(sqlTemplate.SqlBulkMsgCopy, async (writer, t) =>
                {
                    WriteRows(writer, typeCode, messages);

                    return await writer.CompleteAsync(t);

                }, cancellationToken);
            }
            , next: (ex, i) => (ex is NpgsqlException exception) && exception.IsTransient
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
