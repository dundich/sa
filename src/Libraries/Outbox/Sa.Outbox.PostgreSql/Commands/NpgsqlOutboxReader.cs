using Npgsql;
using Sa.Extensions;
using Sa.Outbox.PostgreSql.Configuration;

namespace Sa.Outbox.PostgreSql.Commands;

internal sealed class NpgsqlOutboxReader(PgOutboxTableSettings settings)
{

    public TypeReader Type { get; } = new(settings);

    public MsgReader Message { get; } = new(settings);

    public TaskQueueReader TaskQueue { get; } = new(settings);


    #region NpgsqlDataReader
    public class TaskQueueReader(PgOutboxTableSettings settings)
    {
        /// <summary>
        /// Column positions for one statement execution. <see cref="NpgsqlDataReader.GetOrdinal"/>
        /// does a case-insensitive scan of the statement's field list on every call; the SELECT
        /// shape is fixed per command, so resolving names per row turned ~14 scans per row into a
        /// constant cost. Ordinals are captured once on the first row and every <c>Get*</c> reads
        /// by index afterwards (M3).
        /// </summary>
        public readonly record struct Ordinals(
            int MsgId,
            int MsgPayloadId,
            int TenantId,
            int MsgPart,
            int MsgCreatedAt,
            int TaskId,
            int TaskCreatedAt,
            int DeliveryId,
            int DeliveryAttempt,
            int ErrorId,
            int ConsumerGroup,
            int DeliveryStatusCode,
            int DeliveryStatusMessage,
            int DeliveryCreatedAt)
        {
            public static Ordinals Capture(NpgsqlDataReader reader, PgOutboxTableSettings.TaskQueueTable table)
            {
                var f = table.Fields;
                return new Ordinals(
                    reader.GetOrdinal(f.MsgId),
                    reader.GetOrdinal(f.MsgPayloadId),
                    reader.GetOrdinal(f.TenantId),
                    reader.GetOrdinal(f.MsgPart),
                    reader.GetOrdinal(f.MsgCreatedAt),
                    reader.GetOrdinal(f.TaskId),
                    reader.GetOrdinal(f.TaskCreatedAt),
                    reader.GetOrdinal(f.DeliveryId),
                    reader.GetOrdinal(f.DeliveryAttempt),
                    reader.GetOrdinal(f.ErrorId),
                    reader.GetOrdinal(f.ConsumerGroup),
                    reader.GetOrdinal(f.DeliveryStatusCode),
                    reader.GetOrdinal(f.DeliveryStatusMessage),
                    reader.GetOrdinal(f.DeliveryCreatedAt));
            }
        }

        public Ordinals GetOrdinals(NpgsqlDataReader reader) => Ordinals.Capture(reader, settings.TaskQueue);

        public static Guid GetMgsId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetGuid(o.MsgId);

        public static string GetMgsPayloadId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.MsgPayloadId);

        public static int GetTenantId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.TenantId);

        public static string GetMsgPart(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.MsgPart);

        public static DateTimeOffset GetMsgCreatedAt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.MsgCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public static long GetTaskId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.TaskId);

        public static DateTimeOffset GetTaskCreatedAt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.TaskCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public static long GetDeliveryId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.DeliveryId);

        public static int GetDeliveryAttempt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.DeliveryAttempt);

        public static long GetErrorId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.ErrorId);

        public static string GetConsumerGroup(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.ConsumerGroup);

        public static int GetDeliveryStatusCode(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.DeliveryStatusCode);

        public static string GetDeliveryStatusMessage(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.DeliveryStatusMessage);

        /// <summary>
        /// The column is <c>BIGINT NOT NULL DEFAULT 0</c>, and 0 is its "never delivered"
        /// value — a task that has been rented but not yet finished has no delivery record.
        /// Converting it as a plain Unix timestamp hands the consumer 1970-01-01, which reads
        /// as a real (if absurd) date rather than as the absence of a delivery, so it is mapped
        /// to <see cref="DateTimeOffset.MinValue"/> here. Matches how GetMsgCreatedAt and
        /// GetTaskCreatedAt return a converted value rather than a raw column.
        /// </summary>
        public static DateTimeOffset GetDeliveryCreatedAt(NpgsqlDataReader reader, in Ordinals o)
        {
            var createdAt = reader.GetInt64(o.DeliveryCreatedAt);
            return createdAt == 0 ? DateTimeOffset.MinValue : createdAt.ToDateTimeOffsetFromUnixTimestamp();
        }
    }
    public class TypeReader(PgOutboxTableSettings settings)
    {
        /// <summary>
        /// Column positions for one statement execution, captured once on the first row.
        /// <see cref="NpgsqlDataReader.GetOrdinal"/> scans the statement's field list by
        /// name, so resolving it per row is the same avoidable cost the task queue reader
        /// already removed (M3).
        /// </summary>
        public readonly record struct Ordinals(int TypeId, int TypeName)
        {
            public static Ordinals Capture(NpgsqlDataReader reader, PgOutboxTableSettings.TypeTable table)
            {
                var f = table.Fields;
                return new Ordinals(
                    reader.GetOrdinal(f.TypeId),
                    reader.GetOrdinal(f.TypeName));
            }
        }

        public Ordinals GetOrdinals(NpgsqlDataReader reader) => Ordinals.Capture(reader, settings.Type);

        public static long GetTypeId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.TypeId);

        public static string GetTypeName(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.TypeName);
    }
    public class MsgReader(PgOutboxTableSettings settings)
    {
        /// <summary>
        /// Payload-side column positions, captured once per result set. The payload is the
        /// one column read for every delivered message, so a per-row
        /// <see cref="NpgsqlDataReader.GetOrdinal"/> here was paid on the main delivery path.
        /// </summary>
        public readonly record struct Ordinals(int MsgPayload, int TenantId)
        {
            public static Ordinals Capture(NpgsqlDataReader reader, PgOutboxTableSettings.MessageTable table)
            {
                var f = table.Fields;
                return new Ordinals(
                    reader.GetOrdinal(f.MsgPayload),
                    reader.GetOrdinal(f.TenantId));
            }
        }

        public Ordinals GetOrdinals(NpgsqlDataReader reader) => Ordinals.Capture(reader, settings.Message);

        public static Stream GetMgsPayload(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetStream(o.MsgPayload);

        public static int GetTenantId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.TenantId);
    }

    #endregion
}
