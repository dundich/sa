using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.TypeHashResolve;
using Sa.Outbox.Repository;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class StartDeliveryCommand(
    IPgDataSource dataSource
    , SqlOutboxTemplate template
    , IOutboxMessageSerializer serializer
    , IMsgTypeHashResolver hashResolver
    , TimeProvider timeProvider
) : IStartDeliveryCommand
{

    public async Task<int> FillContext<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {

        int batchSize = writeBuffer.Length;
        long typeCode = await hashResolver.GetCode(filter.PayloadType, cancellationToken);
        var lockOn = filter.ToDate + lockDuration;


        return await dataSource.ExecuteReader(template.SqlLockAndSelect
            , (reader, i) =>
            {
                OutboxDeliveryMessage<TMessage> deliveryMessage = Read<TMessage>(reader, serializer);
                writeBuffer.Span[i] = new OutboxContext<TMessage>(deliveryMessage, timeProvider);
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
        Guid msgId = reader.GetMgsId(template.Settings);
        string payloadId = reader.GetMgsPayloadId(template.Settings);
        int tenantId = reader.GetTenantId(template.Settings);

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
            , reader.GetMsgPart(template.Settings)
            , reader.GetMsgCreatedAt(template.Settings)
        );
    }

    private OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, int tenantId)
    {
        return new OutboxTaskDeliveryInfo(
            reader.GetTaskId(template.Settings)
            , reader.GetDeliveryId(template.Settings)
            , reader.GetDeliveryAttempt(template.Settings)
            , reader.GetErrorId(template.Settings)
            , ReadStatus(reader)
            , ReadTaskPart(reader, tenantId)
        );
    }

    private OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , reader.GetConsumerGroup(template.Settings)
            , reader.GetTaskCreatedAt(template.Settings)
        );
    }

    private TMessage ReadPayload<TMessage>(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
    {
        using Stream stream = reader.GetMgsPayload(template.Settings);
        TMessage payload = serializer.Deserialize<TMessage>(stream)!;
        return payload;
    }

    private DeliveryStatus ReadStatus(NpgsqlDataReader reader)
    {
        int code = reader.GetDeliveryStatusCode(template.Settings);
        string message = reader.GetDeliveryStatusMessage(template.Settings);
        DateTimeOffset createAt = reader.GetDeliveryCreatedAt(template.Settings).ToDateTimeOffsetFromUnixTimestamp();
        return new DeliveryStatus(code, message, createAt);
    }
}
