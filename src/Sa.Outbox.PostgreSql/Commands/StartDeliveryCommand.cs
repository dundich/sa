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
        var lockOn = filter.ToDate + lockDuration;


        return await dataSource.ExecuteReader(sqlTemplate.SqlLockAndSelect
            , (reader, i) =>
            {
                OutboxDeliveryMessage<TMessage> deliveryMessage = DeliveryReader<TMessage>.Read(reader, serializer);
                writeBuffer.Span[i] = deliveryMessage;
            }
            , cmd => cmd
                .AddParamTenantId(filter.TenantId)
                .AddParamMsgPart(filter.Part)
                .AddParamFromDate(filter.FromDate)
                .AddParamToDate(filter.ToDate)
                .AddParamConsumerGroupId(filter.ConsumerGroupId)
                .AddParamTypeId(typeCode)
                .AddParamTransactId(filter.TransactId)
                .AddParamLimit(batchSize)
                .AddParamLockExpiresOn(lockOn)
        
            , cancellationToken);
    }

    internal static class DeliveryReader<TMessage>
    {
        public static OutboxDeliveryMessage<TMessage> Read(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            Guid msgId = reader.GetMgsId();
            string payloadId = reader.GetMgsPayloadId();
            int tenantId = reader.GetTenantId();

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
                , reader.GetMsgPart()
                , reader.GetMsgCreatedAt()
            );
        }

        private static OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, int tenantId)
        {
            return new OutboxTaskDeliveryInfo(
                reader.GetTaskId()
                , reader.GetDeliveryId()
                , reader.GetDeliveryAttempt()
                , reader.GetErrorId()
                , ReadStatus(reader)
                , ReadTaskPart(reader, tenantId)
            );
        }

        private static OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, int tenantId)
        {
            return new OutboxPartInfo(
                tenantId
                , reader.GetConsumerGroup()
                , reader.GetTaskCreatedAt()
            );
        }

        private static TMessage ReadPayload(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
        {
            using Stream stream = reader.GetMgsPayload();
            TMessage payload = serializer.Deserialize<TMessage>(stream)!;
            return payload;
        }

        private static DeliveryStatus ReadStatus(NpgsqlDataReader reader)
        {
            int code = reader.GetDeliveryStatusCode();
            string message = reader.GetDeliveryStatusMessage();
            DateTimeOffset createAt = reader.GetDeliveryCreatedAt().ToDateTimeOffsetFromUnixTimestamp();
            return new DeliveryStatus(code, message, createAt);
        }
    }
}
