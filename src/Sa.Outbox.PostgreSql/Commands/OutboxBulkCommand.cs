using Microsoft.IO;
using Npgsql;
using NpgsqlTypes;
using Sa.Data.PostgreSql;
using Sa.Outbox.PostgreSql.IdGen;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeHashResolve;

namespace Sa.Outbox.PostgreSql.Commands;


internal class OutboxBulkCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate sqlTemplate
    , RecyclableMemoryStreamManager streamManager
    , IOutboxMessageSerializer serializer
    , IIdGenerator idGenerator
    , IMsgTypeHashResolver hashResolver
) : IOutboxBulkCommand
{
    public async ValueTask<ulong> BulkWrite<TMessage>(string payloadType, ReadOnlyMemory<OutboxMessage<TMessage>> messages, CancellationToken cancellationToken)
    {
        long typeCode = await hashResolver.GetCode(payloadType, cancellationToken);

        ulong result = await dataSource.BeginBinaryImport(sqlTemplate.SqlBulkOutboxCopy, async (writer, t) =>
        {
            WriteRows(writer, typeCode, messages);

            return await writer.CompleteAsync(t);

        }, cancellationToken);

        return result;
    }

    private void WriteRows<TMessage>(NpgsqlBinaryImporter writer, long payloadTypeCode, ReadOnlyMemory<OutboxMessage<TMessage>> messages)
    {
        foreach (OutboxMessage<TMessage> row in messages.Span)
        {
            string id = idGenerator.GenId(row.PartInfo.CreatedAt);

            writer.StartRow();


            // id
            writer.Write(id, NpgsqlDbType.Char);
            // tenant
            writer.Write(row.PartInfo.TenantId, NpgsqlDbType.Integer);
            // part
            writer.Write(row.PartInfo.Part, NpgsqlDbType.Text);


            // payload_id
            writer.Write(row.PayloadId, NpgsqlDbType.Text);
            // payload_type
            writer.Write(payloadTypeCode, NpgsqlDbType.Bigint);
            // payload
            using RecyclableMemoryStream stream = streamManager.GetStream();
            serializer.Serialize(stream, row.Payload);
            stream.Position = 0;
            writer.Write(stream, NpgsqlDbType.Bytea);
            // payload_size
            writer.Write(stream.Length, NpgsqlDbType.Integer);


            // created_at
            writer.Write(row.PartInfo.CreatedAt.ToUnixTimeSeconds(), NpgsqlDbType.Bigint);
        }
    }
}
