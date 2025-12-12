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
            new(SqlParam.TenantId, filter.TenantId)
            , new(SqlParam.MsgPart, filter.Part)
            , new(SqlParam.FromDate, filter.FromDate.ToUnixTimeSeconds())
            , new(SqlParam.ConsumerGroupId, filter.ConsumerGroupId)
            , new(SqlParam.MsgPayloadType, typeCode)
            , new(SqlParam.TransactId, filter.TransactId)
            , new(SqlParam.Limit, batchSize)
            , new(SqlParam.LockExpiresOn, (filter.ToDate + lockDuration).ToUnixTimeSeconds())
            , new(SqlParam.NowDate, filter.ToDate.ToUnixTimeSeconds())
        ]
        , cancellationToken);
    }

    internal static class DeliveryReader<TMessage>
    {
        public static OutboxDeliveryMessage<TMessage> Read(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            string msgId = reader.GetString("msg_id");
            string payloadId = reader.GetString("msg_payload_id");
            int tenantId = reader.GetInt32("tenant_id");

            TMessage payload = ReadPayload(reader, serializer);
            OutboxPartInfo outboxPart = ReadOutboxMsgPart(reader, tenantId);
            OutboxTaskDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader, tenantId);

            OutboxMessage<TMessage> msg = new(payloadId, payload, outboxPart);

            return new OutboxDeliveryMessage<TMessage>(msgId, msg, deliveryInfo);
        }

        private static OutboxPartInfo ReadOutboxMsgPart(NpgsqlDataReader reader, int tenantId)
        {
            return new OutboxPartInfo(
                tenantId
                , reader.GetString("msg_part")
                , reader.GetInt64("msg_created_at").ToDateTimeOffsetFromUnixTimestamp()
            );
        }

        private static OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, int tenantId)
        {
            return new OutboxTaskDeliveryInfo(
                reader.GetInt64("task_id")
                , reader.GetInt64("delivery_id")
                , reader.GetInt32("delivery_attempt")
                , reader.GetString("error_id")
                , ReadStatus(reader)
                , ReadTaskPart(reader, tenantId)
            );
        }

        private static OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, int tenantId)
        {
            return new OutboxPartInfo(
                tenantId
                , reader.GetString("consumer_group")
                , reader.GetInt64("task_created_at").ToDateTimeOffsetFromUnixTimestamp()
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
