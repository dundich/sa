using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeHashResolve;
using System.Data;

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
            new("tenant", filter.TenantId)
            , new("part", filter.Part)
            , new("from_date", filter.FromDate.ToUnixTimeSeconds())
            , new("payload_type", typeCode)
            , new("transact_id", filter.TransactId)
            , new("limit", batchSize)
            , new("lock_expires_on", (filter.NowDate + lockDuration).ToUnixTimeSeconds())
            , new("now", filter.NowDate.ToUnixTimeSeconds())
        ]
        , cancellationToken);
    }

    internal static class DeliveryReader<TMessage>
    {
        public static OutboxDeliveryMessage<TMessage> Read(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            string outboxId = reader.GetString("outbox_id");
            string payloadId = reader.GetString("outbox_payload_id");

            TMessage payload = ReadPayload(reader, serializer);
            OutboxPartInfo outboxPart = ReadOutboxPart(reader);
            OutboxDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader);

            return new OutboxDeliveryMessage<TMessage>(outboxId, payloadId, payload, outboxPart, deliveryInfo);
        }


        private static OutboxPartInfo ReadOutboxPart(NpgsqlDataReader reader)
        {
            return new OutboxPartInfo(
                reader.GetInt32("outbox_tenant")
                , reader.GetString("outbox_part")
                , reader.GetInt64("outbox_created_at").ToDateTimeOffsetFromUnixTimestamp()
            );
        }

        private static OutboxDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader)
        {
            return new OutboxDeliveryInfo(
                reader.GetString("outbox_delivery_id")
                , reader.GetInt32("outbox_delivery_attempt")
                , reader.GetString("outbox_delivery_error_id")
                , ReadStatus(reader)
                , reader.GetInt64("outbox_delivery_created_at").ToDateTimeOffsetFromUnixTimestamp()
            );
        }


        private static TMessage ReadPayload(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            using Stream stream = reader.GetStream("outbox_payload");
            TMessage payload = serializer.Deserialize<TMessage>(stream)!;
            return payload;
        }


        private static DeliveryStatus ReadStatus(NpgsqlDataReader reader)
        {
            int code = reader.GetInt32("outbox_delivery_status_code");
            string message = reader.GetString("outbox_delivery_status_message");
            DateTimeOffset createAt = reader.GetInt64("outbox_delivery_created_at").ToDateTimeOffsetFromUnixTimestamp();
            return new DeliveryStatus(code, message, createAt);
        }
    }
}
