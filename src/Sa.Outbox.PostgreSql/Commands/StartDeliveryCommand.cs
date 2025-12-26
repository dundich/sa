using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeResolve;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class StartDeliveryCommand(
    IOutboxContextFactory contextFactory
    , IPgDataSource dataSource
    , SqlOutboxTemplate template
    , IOutboxMessageSerializer serializer
    , IOutboxTypeResolver hashResolver
    , NpqsqlOutboxReader outboxReader
) : IStartDeliveryCommand
{

    public async Task<int> ExecuteFill<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {

        int batchSize = writeBuffer.Length;
        long typeCode = await hashResolver.GetHashCode(filter.PayloadType, cancellationToken);
        var lockOn = filter.ToDate + lockDuration;


        return await dataSource.ExecuteReader(template.SqlLockAndSelect
            , (reader, i) =>
            {
                OutboxDeliveryMessage<TMessage> deliveryMessage = Read<TMessage>(reader, serializer);
                writeBuffer.Span[i] = contextFactory.Create<TMessage>(deliveryMessage);
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


    private OutboxDeliveryMessage<TMessage> Read<TMessage>(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
    {
        Guid msgId = outboxReader.TaskQueue.GetMgsId(reader);
        string payloadId = outboxReader.TaskQueue.GetMgsPayloadId(reader);
        int tenantId = outboxReader.TaskQueue.GetTenantId(reader);

        TMessage payload = ReadPayload<TMessage>(reader, serializer);
        OutboxPartInfo outboxPart = ReadOutboxMsgPart(reader, tenantId);
        OutboxTaskDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader, tenantId);

        OutboxMessage<TMessage> msg = new(payloadId, payload, outboxPart);

        return new OutboxDeliveryMessage<TMessage>(msgId, msg, deliveryInfo);
    }

    private OutboxPartInfo ReadOutboxMsgPart(NpgsqlDataReader reader, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , outboxReader.TaskQueue.GetMsgPart(reader)
            , outboxReader.TaskQueue.GetMsgCreatedAt(reader)
        );
    }

    private OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, int tenantId)
    {
        return new OutboxTaskDeliveryInfo(
            outboxReader.TaskQueue.GetTaskId(reader)
            , outboxReader.TaskQueue.GetDeliveryId(reader)
            , outboxReader.TaskQueue.GetDeliveryAttempt(reader)
            , outboxReader.TaskQueue.GetErrorId(reader)
            , ReadStatus(reader)
            , ReadTaskPart(reader, tenantId)
        );
    }

    private OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , outboxReader.TaskQueue.GetConsumerGroup(reader)
            , outboxReader.TaskQueue.GetTaskCreatedAt(reader)
        );
    }

    private TMessage ReadPayload<TMessage>(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
    {
        using Stream stream = outboxReader.Message.GetMgsPayload(reader);
        TMessage payload = serializer.Deserialize<TMessage>(stream)!;
        return payload;
    }

    private DeliveryStatus ReadStatus(NpgsqlDataReader reader)
    {
        int code = outboxReader.TaskQueue.GetDeliveryStatusCode(reader);
        string message = outboxReader.TaskQueue.GetDeliveryStatusMessage(reader);
        DateTimeOffset createAt = outboxReader.TaskQueue.GetDeliveryCreatedAt(reader).ToDateTimeOffsetFromUnixTimestamp();
        return new DeliveryStatus((DeliveryStatusCode)code, message, createAt);
    }
}
