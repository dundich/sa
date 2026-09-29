using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Serialization;
using Sa.Outbox.PostgreSql.SqlBuilder;
using Sa.Outbox.PostgreSql.TypeResolve;

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
        long typeCode = await hashResolver.GetHashCode(filter.PayloadType, cancellationToken);
        var lockOn = filter.NowDate + lockDuration;

        // Column ordinals are resolved once, on the first row, and read by index afterwards (M3).
        // The locals live in this invocation's closure and the callback runs synchronously inside
        // ExecuteReader, so the singleton command instance needs no synchronization.
        NpgsqlOutboxReader.TaskQueueReader.Ordinals ordinals = default;
        bool haveOrdinals = false;

        return await dataSource.ExecuteReader(sql.SqlLockAndSelect
            , (reader, i) =>
            {
                if (!haveOrdinals)
                {
                    ordinals = outboxReader.TaskQueue.GetOrdinals(reader);
                    haveOrdinals = true;
                }

                OutboxDeliveryMessage<TMessage> deliveryMessage = Read<TMessage>(reader, in ordinals, serializer);
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


    private OutboxDeliveryMessage<TMessage> Read<TMessage>(
        NpgsqlDataReader reader,
        in NpgsqlOutboxReader.TaskQueueReader.Ordinals o,
        IOutboxMessageSerializer serializer)
    {
        Guid msgId = outboxReader.TaskQueue.GetMgsId(reader, in o);
        string payloadId = outboxReader.TaskQueue.GetMgsPayloadId(reader, in o);
        int tenantId = outboxReader.TaskQueue.GetTenantId(reader, in o);

        TMessage payload = ReadPayload<TMessage>(reader, serializer);
        OutboxPartInfo outboxPart = ReadOutboxMsgPart(reader, in o, tenantId);
        OutboxTaskDeliveryInfo deliveryInfo = ReadDeliveryInfo(reader, in o, tenantId);

        OutboxMessage<TMessage> msg = new(payloadId, payload, outboxPart);

        return new OutboxDeliveryMessage<TMessage>(msgId, msg, deliveryInfo);
    }

    private OutboxPartInfo ReadOutboxMsgPart(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , outboxReader.TaskQueue.GetMsgPart(reader, in o)
            , outboxReader.TaskQueue.GetMsgCreatedAt(reader, in o)
        );
    }

    private OutboxTaskDeliveryInfo ReadDeliveryInfo(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxTaskDeliveryInfo(
            outboxReader.TaskQueue.GetTaskId(reader, in o)
            , outboxReader.TaskQueue.GetDeliveryId(reader, in o)
            , outboxReader.TaskQueue.GetDeliveryAttempt(reader, in o)
            , outboxReader.TaskQueue.GetErrorId(reader, in o)
            , ReadStatus(reader, in o)
            , ReadTaskPart(reader, in o, tenantId)
        );
    }

    private OutboxPartInfo ReadTaskPart(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o, int tenantId)
    {
        return new OutboxPartInfo(
            tenantId
            , outboxReader.TaskQueue.GetConsumerGroup(reader, in o)
            , outboxReader.TaskQueue.GetTaskCreatedAt(reader, in o)
        );
    }

    private TMessage ReadPayload<TMessage>(NpgsqlDataReader reader, IOutboxMessageSerializer serializer)
    {
        using Stream stream = outboxReader.Message.GetMgsPayload(reader);
        TMessage payload = serializer.Deserialize<TMessage>(stream)!;
        return payload;
    }

    private DeliveryStatus ReadStatus(NpgsqlDataReader reader, in NpgsqlOutboxReader.TaskQueueReader.Ordinals o)
    {
        int code = outboxReader.TaskQueue.GetDeliveryStatusCode(reader, in o);
        string message = outboxReader.TaskQueue.GetDeliveryStatusMessage(reader, in o);
        DateTimeOffset createAt = outboxReader.TaskQueue.GetDeliveryCreatedAt(reader, in o);
        return new DeliveryStatus((DeliveryStatusCode)code, message, createAt);
    }
}
