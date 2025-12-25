using System.Data;
using System.Text;
using Npgsql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class NpqsqlOutboxReader(PgOutboxTableSettings settings)
{

    public TypeReader Type { get; } = new(settings);

    public MsgReader Message { get; } = new(settings);

    public TaskQueueReader TaskQueue { get; } = new(settings);


    #region NpgsqlDataReader
    public class TaskQueueReader(PgOutboxTableSettings settings)
    {
        public Guid GetMgsId(NpgsqlDataReader reader)
            => reader.GetGuid(settings.TaskQueue.Fields.MsgId);

        public string GetMgsPayloadId(NpgsqlDataReader reader)
            => reader.GetString(settings.TaskQueue.Fields.MsgPayloadId);

        public int GetTenantId(NpgsqlDataReader reader)
            => reader.GetInt32(settings.TaskQueue.Fields.TenantId);

        public string GetMsgPart(NpgsqlDataReader reader)
            => reader.GetString(settings.TaskQueue.Fields.MsgPart);

        public DateTimeOffset GetMsgCreatedAt(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.MsgCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public long GetTaskId(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.TaskId);

        public DateTimeOffset GetTaskCreatedAt(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.TaskCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public long GetDeliveryId(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.DeliveryId);

        public int GetDeliveryAttempt(NpgsqlDataReader reader)
            => reader.GetInt32(settings.TaskQueue.Fields.DeliveryAttempt);

        public long GetErrorId(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.ErrorId);

        public string GetConsumerGroup(NpgsqlDataReader reader)
            => reader.GetString(settings.TaskQueue.Fields.ConsumerGroup);

        public int GetDeliveryStatusCode(NpgsqlDataReader reader)
            => reader.GetInt32(settings.TaskQueue.Fields.DeliveryStatusCode);

        public string GetDeliveryStatusMessage(NpgsqlDataReader reader)
            => reader.GetString(settings.TaskQueue.Fields.DeliveryStatusMessage);

        public long GetDeliveryCreatedAt(NpgsqlDataReader reader)
            => reader.GetInt64(settings.TaskQueue.Fields.DeliveryCreatedAt);
    }

    public class TypeReader(PgOutboxTableSettings settings)
    {
        public long GetTypeId(NpgsqlDataReader reader)
            => reader.GetInt64(settings.Type.Fields.TypeId);

        public string GetTypeName(NpgsqlDataReader reader)
            => reader.GetString(settings.Type.Fields.TypeName);
    }

    public class MsgReader(PgOutboxTableSettings settings)
    {
        public Stream GetMgsPayload(NpgsqlDataReader reader)
            => reader.GetStream(settings.Message.Fields.MsgPayload);
    }

    #endregion
}
