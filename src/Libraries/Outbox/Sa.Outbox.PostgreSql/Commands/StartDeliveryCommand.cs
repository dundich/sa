using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;
using Sa.Outbox.Serialization;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class StartDeliveryCommand(
    IOutboxContextFactory contextFactory
    , IPgDataSource dataSource
    , SqlOutboxBuilder sql
    , IOutboxMessageSerializer serializer
    , IOutboxTypeResolver hashResolver
    , NpgsqlOutboxReader outboxReader) : IStartDeliveryCommand
{

    public async Task<int> ExecuteFill<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {

        int batchSize = writeBuffer.Length;
        long typeCode = await hashResolver.GetHashCodeAsync(filter.PayloadType, cancellationToken);
        var lockOn = filter.NowDate + lockDuration;

        // Column ordinals are resolved once, on the first row, and read by index afterwards (M3).
        // The locals live in this invocation's closure and the callback runs synchronously inside
        // ExecuteReader, so the singleton command instance needs no synchronization. Both halves
        // of the joined row are captured here: the payload column is read for every message, and
        // its per-row GetOrdinal was the remaining scan on the delivery path.
        NpgsqlOutboxReader.TaskQueueReader.Ordinals ordinals = default;
        NpgsqlOutboxReader.MsgReader.Ordinals msgOrdinals = default;
        bool haveOrdinals = false;

        return await dataSource.ExecuteReaderAsync(sql.SqlLockAndSelect
            , (reader, i) =>
            {
                if (!haveOrdinals)
                {
                    ordinals = outboxReader.TaskQueue.GetOrdinals(reader);
                    msgOrdinals = outboxReader.Message.GetOrdinals(reader);
                    haveOrdinals = true;
                }

                OutboxDeliveryMessage<TMessage> deliveryMessage =
                    Read<TMessage>(reader, in ordinals, in msgOrdinals, serializer);
                writeBuffer.Span[i] = contextFactory.Create<TMessage>(deliveryMessage);
            }
            , cmd => cmd
                .AddParamTenantId(filter.TenantId)
                .AddParamMsgPart(filter.Part)
                .AddParamWindowFrom(filter.FromDate)
                .AddParamRetryAfter(filter.ToDate)
                .AddParamConsumerGroupId(filter.ConsumerGroupId)
                .AddParamTypeId(typeCode)
                .AddParamTransactId(filter.TransactId)
                .AddParamLimit(batchSize)
                .AddParamLockExpiresOn(lockOn)

            , cancellationToken);
    }


    private static OutboxDeliveryMessage<TMessage> Read<TMessage>(
        NpgsqlDataReader reader,
        in NpgsqlOutboxReader.TaskQueueReader.Ordinals o,
        in NpgsqlOutboxReader.MsgReader.Ordinals msgOrdinals,
        IOutboxMessageSerializer serializer)
    {
        Guid msgId = NpgsqlOutboxReader.TaskQueueReader.GetMgsId(reader, in o);
        string payloadId = NpgsqlOutboxReader.TaskQueueReader.GetMgsPayloadId(reader, in o);
        int tenantId = NpgsqlOutboxReader.TaskQueueReader.GetTenantId(reader, in o);

        TMessage payload = ReadPayload<TMessage>(reader, in msgOrdinals, serializer);
        OutboxPartInfo outboxPart = ReadOutboxMsgPart(reader, in o, tenantId);
        OutboxTaskDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader, in o, tenantId);

        // The rent token is the provider's own row identity: task + delivery attempt. The
        // pipeline hands it back on ReturnDelivery, so this command never has to reconstruct it
        // from the public OutboxId or from DeliveryInfo.
        return new OutboxDeliveryMessage<TMessage>(
            OutboxId: msgId,
            PayloadId: payloadId,
            Payload: payload,
            PartInfo: outboxPart,
            DeliveryInfo: deliveryInfo,
            RentToken: new OutboxRentToken(deliveryInfo.TaskId, deliveryInfo.DeliveryId));
    }

    private static OutboxPartInfo ReadOutboxMsgPart(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , NpgsqlOutboxReader.TaskQueueReader.GetMsgPart(reader, in o)
            , NpgsqlOutboxReader.TaskQueueReader.GetMsgCreatedAt(reader, in o)
        );
    }

    private static OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxTaskDeliveryInfo(
            NpgsqlOutboxReader.TaskQueueReader.GetTaskId(reader, in o)
            , NpgsqlOutboxReader.TaskQueueReader.GetDeliveryId(reader, in o)
            , NpgsqlOutboxReader.TaskQueueReader.GetDeliveryAttempt(reader, in o)
            , NpgsqlOutboxReader.TaskQueueReader.GetErrorId(reader, in o)
            , ReadStatus(reader, in o)
            , ReadTaskPart(reader, in o, tenantId)
        );
    }

    private static OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , NpgsqlOutboxReader.TaskQueueReader.GetConsumerGroup(reader, in o)
            , NpgsqlOutboxReader.TaskQueueReader.GetTaskCreatedAt(reader, in o)
        );
    }

    private static TMessage ReadPayload<TMessage>(
        NpgsqlDataReader reader,
        in NpgsqlOutboxReader.MsgReader.Ordinals msgOrdinals,
        IOutboxMessageSerializer serializer)
    {
        using Stream stream = NpgsqlOutboxReader.MsgReader.GetMgsPayload(reader, in msgOrdinals);
        TMessage payload = serializer.Deserialize<TMessage>(stream)!;
        return payload;
    }

    private static DeliveryStatus ReadStatus(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o)
    {
        int code = NpgsqlOutboxReader.TaskQueueReader.GetDeliveryStatusCode(reader, in o);
        string message = NpgsqlOutboxReader.TaskQueueReader.GetDeliveryStatusMessage(reader, in o);
        DateTimeOffset createAt = NpgsqlOutboxReader.TaskQueueReader.GetDeliveryCreatedAt(reader, in o);
        return new DeliveryStatus((DeliveryStatusCode)code, message, createAt);
    }
}
