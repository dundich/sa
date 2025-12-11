using System.Data;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeHashResolve;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class StartDeliveryCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate sqlTemplate
    , IOutboxMessageSerializer serializer
    , IMsgTypeHashResolver hashResolver
) : IStartDeliveryCommand
{

    public async Task<int> Execute<TMessage>(Memory<OutboxDeliveryMessage<TMessage>> writeBuffer, int batchSize, TimeSpan lockDuration, OutboxMessageFilter filter, CancellationToken cancellationToken)
    {

        long typeCode = await hashResolver.GetCode(filter.PayloadType, cancellationToken);


        return await dataSource.ExecuteReader(sqlTemplate.SqlLockAndSelect, (reader, i) =>
        {
            OutboxDeliveryMessage<TMessage> deliveryMessage = DeliveryReader<TMessage>.Read(reader, serializer);

            writeBuffer.Span[i] = deliveryMessage;
        },
        [
            new(CachedSqlParamNames.TenantId, filter.TenantId)
            , new(CachedSqlParamNames.MsgPart, filter.Part)
            , new(CachedSqlParamNames.FromDate, filter.FromDate.ToUnixTimeSeconds())
            , new(CachedSqlParamNames.MsgPayloadType, typeCode)
            , new(CachedSqlParamNames.DeliveryTransactId, filter.TransactId)
            , new(CachedSqlParamNames.Limit, batchSize)
            , new(CachedSqlParamNames.LockExpiresOn, (filter.ToDate + lockDuration).ToUnixTimeSeconds())
            , new(CachedSqlParamNames.NowDate, filter.ToDate.ToUnixTimeSeconds())
        ]
        , cancellationToken);
    }

    internal static class DeliveryReader<TMessage>
    {
        public static OutboxDeliveryMessage<TMessage> Read(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            string outboxId = reader.GetString("msg_id");
            string payloadId = reader.GetString("msg_payload_id");

            TMessage payload = ReadPayload(reader, serializer);
            OutboxPartInfo outboxPart = ReadOutboxPart(reader);
            OutboxDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader);

            OutboxMessage<TMessage> msg = new(payloadId, payload, outboxPart);

            return new OutboxDeliveryMessage<TMessage>(outboxId, msg, deliveryInfo);
        }

        private static OutboxPartInfo ReadOutboxPart(NpgsqlDataReader reader)
        {
            return new OutboxPartInfo(
                reader.GetInt32("msg_tenant")
                , reader.GetString("msg_part")
                , reader.GetInt64("msg_created_at").ToDateTimeOffsetFromUnixTimestamp()
            );
        }

        private static OutboxDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader)
        {
            return new OutboxDeliveryInfo(
                reader.GetString("delivery_id")
                , reader.GetInt32("delivery_attempt")
                , reader.GetString("error_id")
                , ReadStatus(reader)
            );
        }

        private static TMessage ReadPayload(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            using Stream stream = reader.GetStream("msg_payload");
            TMessage payload = serializer.Deserialize<TMessage>(stream)!;
            return payload;
        }

        private static DeliveryStatus ReadStatus(NpgsqlDataReader reader)
        {
            int code = reader.GetInt32("delivery_status_code");
            string message = reader.GetString("delivery_status_message");
            DateTimeOffset createAt = reader.GetInt64("delivery_created_at").ToDateTimeOffsetFromUnixTimestamp();
            return new DeliveryStatus(code, message, createAt);
        }
    }
}
