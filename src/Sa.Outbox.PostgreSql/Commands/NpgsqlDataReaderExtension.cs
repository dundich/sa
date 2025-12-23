using System.Data;
using System.Text;
using Npgsql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql.Commands;

internal static class NpgsqlDataReaderExtension
{
    public static Guid GetMgsId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetGuid(settings.Message.Fields.MsgId);

    public static string GetMgsPayloadId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetString(settings.Message.Fields.MsgPayloadId);

    public static Stream GetMgsPayload(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetStream(settings.Message.Fields.MsgPayload);

    public static int GetTenantId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt32(settings.Message.Fields.TenantId);

    public static string GetMsgPart(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetString(settings.Message.Fields.MsgPart);

    public static DateTimeOffset GetMsgCreatedAt(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.Message.Fields.MsgCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

    public static long GetTaskId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.TaskQueue.Fields.TaskId);

    public static DateTimeOffset GetTaskCreatedAt(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.TaskQueue.Fields.TaskCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

    public static long GetDeliveryId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.TaskQueue.Fields.DeliveryId);

    public static int GetDeliveryAttempt(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt32(settings.TaskQueue.Fields.DeliveryAttempt);

    public static long GetErrorId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.TaskQueue.Fields.ErrorId);

    public static string GetConsumerGroup(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetString(settings.TaskQueue.Fields.ConsumerGroup);

    public static int GetDeliveryStatusCode(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt32(settings.TaskQueue.Fields.DeliveryStatusCode);

    public static string GetDeliveryStatusMessage(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetString(settings.TaskQueue.Fields.DeliveryStatusMessage);

    public static long GetDeliveryCreatedAt(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.TaskQueue.Fields.DeliveryCreatedAt);

    public static long GetTypeId(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetInt64(settings.Type.Fields.TypeId);

    public static string GetTypeName(this NpgsqlDataReader reader, PgOutboxTableSettings settings)
        => reader.GetString(settings.Type.Fields.TypeName);
}
