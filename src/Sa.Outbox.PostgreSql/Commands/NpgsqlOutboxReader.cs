using System.Data;
using System.Text;
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

        public Guid GetMgsId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetGuid(o.MsgId);

        public string GetMgsPayloadId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.MsgPayloadId);

        public int GetTenantId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.TenantId);

        public string GetMsgPart(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.MsgPart);

        public DateTimeOffset GetMsgCreatedAt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.MsgCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public long GetTaskId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.TaskId);

        public DateTimeOffset GetTaskCreatedAt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.TaskCreatedAt).ToDateTimeOffsetFromUnixTimestamp();

        public long GetDeliveryId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.DeliveryId);

        public int GetDeliveryAttempt(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.DeliveryAttempt);

        public long GetErrorId(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt64(o.ErrorId);

        public string GetConsumerGroup(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.ConsumerGroup);

        public int GetDeliveryStatusCode(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetInt32(o.DeliveryStatusCode);

        public string GetDeliveryStatusMessage(NpgsqlDataReader reader, in Ordinals o)
            => reader.GetString(o.DeliveryStatusMessage);

        /// <summary>
        /// The column is <c>BIGINT NOT NULL DEFAULT 0</c>, and 0 is its "never delivered"
        /// value — a task that has been rented but not yet finished has no delivery record.
        /// Converting it as a plain Unix timestamp hands the consumer 1970-01-01, which reads
        /// as a real (if absurd) date rather than as the absence of a delivery, so it is mapped
        /// to <see cref="DateTimeOffset.MinValue"/> here. Matches how GetMsgCreatedAt and
        /// GetTaskCreatedAt return a converted value rather than a raw column.
        /// </summary>
        public DateTimeOffset GetDeliveryCreatedAt(NpgsqlDataReader reader, in Ordinals o)
        {
            var createdAt = reader.GetInt64(o.DeliveryCreatedAt);
            return createdAt == 0 ? DateTimeOffset.MinValue : createdAt.ToDateTimeOffsetFromUnixTimestamp();
        }
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

        public int GetTenantId(NpgsqlDataReader reader)
            => reader.GetInt32(settings.Message.Fields.TenantId);
    }

    #endregion
}
