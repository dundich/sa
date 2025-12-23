using System.Data;
using System.Text;
using Npgsql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal static class NpgsqlDataReaderExtension
{
    public static Guid GetMgsId(this NpgsqlDataReader reader)
        => reader.GetGuid(OutboxTableFields.Message.MsgId);

    public static string GetMgsPayloadId(this NpgsqlDataReader reader)
        => reader.GetString(OutboxTableFields.Message.MsgPayloadId);

    public static Stream GetMgsPayload(this NpgsqlDataReader reader)
        => reader.GetStream(OutboxTableFields.Message.MsgPayload);

    public static int GetTenantId(this NpgsqlDataReader reader)
        => reader.GetInt32(OutboxTableFields.Message.TenantId);

    public static string GetMsgPart(this NpgsqlDataReader reader)
        => reader.GetString(OutboxTableFields.Message.MsgPart);

    public static DateTimeOffset GetMsgCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.Message.MsgCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

    public static long GetTaskId(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TaskQueue.TaskId);

    public static DateTimeOffset GetTaskCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TaskQueue.TaskCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

    public static long GetDeliveryId(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TaskQueue.DeliveryId);

    public static int GetDeliveryAttempt(this NpgsqlDataReader reader)
        => reader.GetInt32(OutboxTableFields.TaskQueue.DeliveryAttempt);

    public static long GetErrorId(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TaskQueue.ErrorId);

    public static string GetConsumerGroup(this NpgsqlDataReader reader)
        => reader.GetString(OutboxTableFields.TaskQueue.ConsumerGroup);

    public static int GetDeliveryStatusCode(this NpgsqlDataReader reader)
        => reader.GetInt32(OutboxTableFields.TaskQueue.DeliveryStatusCode);

    public static string GetDeliveryStatusMessage(this NpgsqlDataReader reader)
        => reader.GetString(OutboxTableFields.TaskQueue.DeliveryStatusMessage);

    public static long GetDeliveryCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TaskQueue.DeliveryCreatedAt);

    public static long GetTypeId(this NpgsqlDataReader reader)
        => reader.GetInt64(OutboxTableFields.TypeTable.TypeId);

    public static string GetTypeName(this NpgsqlDataReader reader)
        => reader.GetString(OutboxTableFields.TypeTable.TypeName);
}
