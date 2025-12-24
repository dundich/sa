namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Represents the settings for configuring the Outbox tables in PostgreSQL.
/// </summary>
public sealed class PgOutboxTableSettings
{
    public static class Defaults
    {
        public const string DatabaseSchemaName = "public";
        public const string DatabaseTableName = "outbox";
    }

    /// <summary>
    /// Gets or sets the name of the database schema.
    /// Default is set to "public".
    /// </summary>
    public string DatabaseSchemaName { get; set; } = Defaults.DatabaseSchemaName;


    public TaskQueueTable TaskQueue { get; } = new();

    public MessageTable Message { get; } = new();

    public DeliveryTable Delivery { get; } = new();

    public TypeTable Type { get; } = new();

    public OffsetTable Offset { get; } = new();


    public ErrorTable Error { get; } = new();

    /// <summary>
    /// Message table fields (read-only table)
    /// </summary>
    public sealed class MessageTable
    {
        public const string Suffix = "__msg$";

        public string TableName { get; set; } = $"{Defaults.DatabaseTableName}{Suffix}";

        public int FillFactor { get; set; } = 100;

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string MsgId { get; set; } = "msg_id";
            public string TenantId { get; set; } = "tenant_id";
            public string MsgPart { get; set; } = "msg_part";
            public string MsgPayloadId { get; set; } = "msg_payload_id";
            public string MsgPayloadType { get; set; } = "msg_payload_type";
            public string MsgPayload { get; set; } = "msg_payload";
            public string MsgPayloadSize { get; set; } = "msg_payload_size";
            public string MsgCreatedAt { get; set; } = "msg_created_at";

            public string[] All() =>
            [
                $"{MsgId} UUID NOT NULL, -- UUID v7 is gen by app",
                $"{TenantId} INT NOT NULL DEFAULT 0",
                $"{MsgPart} TEXT NOT NULL",
                $"{MsgPayloadId} TEXT NOT NULL DEFAULT ''",
                $"{MsgPayloadType} BIGINT NOT NULL",
                $"{MsgPayload} BYTEA NOT NULL",
                $"{MsgPayloadSize} INT NOT NULL",
                $"{MsgCreatedAt} BIGINT NOT NULL DEFAULT 0"
            ];
        }
    }

    /// <summary>
    /// Task queue table fields (read-write table)
    /// </summary>
    public sealed class TaskQueueTable
    {
        public string TableName { get; set; } = Defaults.DatabaseTableName;

        public int FillFactor { get; set; } = 50;

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string TaskId { get; set; } = "task_id";
            public string ConsumerGroup { get; set; } = "consumer_group";
            public string TaskLockExpiresOn { get; set; } = "task_lock_expires_on";
            public string TaskTransactId { get; set; } = "task_transact_id";

            // Message references
            public string MsgId { get; set; } = "msg_id";
            public string MsgPart { get; set; } = "msg_part";
            public string TenantId { get; set; } = "tenant_id";
            public string MsgPayloadId { get; set; } = "msg_payload_id";
            public string MsgCreatedAt { get; set; } = "msg_created_at";

            // Delivery information
            public string DeliveryId { get; set; } = "delivery_id";
            public string DeliveryAttempt { get; set; } = "delivery_attempt";
            public string DeliveryStatusCode { get; set; } = "delivery_status_code";
            public string DeliveryStatusMessage { get; set; } = "delivery_status_message";
            public string DeliveryCreatedAt { get; set; } = "delivery_created_at";

            // Error reference
            public string ErrorId { get; set; } = "error_id";
            public string TaskCreatedAt { get; set; } = "task_created_at";

            public string[] All() =>
            [
                $"{TaskId} BIGSERIAL NOT NULL",
                $"{ConsumerGroup} TEXT NOT NULL",
                $"{TaskLockExpiresOn} BIGINT NOT NULL DEFAULT 0",
                $"{TaskTransactId} TEXT NOT NULL DEFAULT ''",
                $"{MsgId} UUID NOT NULL",
                $"{MsgPart} TEXT NOT NULL",
                $"{TenantId} INT NOT NULL DEFAULT 0",
                $"{MsgPayloadId} TEXT NOT NULL",
                $"{MsgCreatedAt} BIGINT NOT NULL DEFAULT 0",
                $"{DeliveryId} BIGINT NOT NULL DEFAULT 0",
                $"{DeliveryAttempt} int NOT NULL DEFAULT 0",
                $"{DeliveryStatusCode} INT NOT NULL DEFAULT {Sa.Outbox.DeliveryStatusCode.Pending}",
                $"{DeliveryStatusMessage} TEXT NOT NULL DEFAULT ''",
                $"{DeliveryCreatedAt} BIGINT NOT NULL DEFAULT 0",
                $"{ErrorId} BIGINT NOT NULL DEFAULT 0",
                $"{TaskCreatedAt} BIGINT NOT NULL DEFAULT 0"
            ];
        }
    }

    /// <summary>
    /// Delivery table fields (read-only table)
    /// </summary>
    public sealed class DeliveryTable
    {
        public const string Suffix = "__log$";

        public int FillFactor { get; set; } = 100;

        public string TableName { get; set; } = $"{Defaults.DatabaseTableName}{Suffix}";

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string DeliveryId { get; set; } = "delivery_id";
            public string DeliveryStatusCode { get; set; } = "delivery_status_code";
            public string DeliveryStatusMessage { get; set; } = "delivery_status_message";
            public string MsgPayloadId { get; set; } = "msg_payload_id";
            public string TenantId { get; set; } = "tenant_id";
            public string ConsumerGroup { get; set; } = "consumer_group";
            public string TaskId { get; set; } = "task_id";
            public string TaskCreatedAt { get; set; } = "task_created_at";
            public string TaskTransactId { get; set; } = "task_transact_id";
            public string TaskLockExpiresOn { get; set; } = "task_lock_expires_on";
            public string ErrorId { get; set; } = "error_id";
            public string DeliveryCreatedAt { get; set; } = "delivery_created_at";

            public string[] All() =>
            [
                $"{DeliveryId} BIGSERIAL NOT NULL",
                $"{DeliveryStatusCode} INT NOT NULL DEFAULT {Sa.Outbox.DeliveryStatusCode.Pending}",
                $"{DeliveryStatusMessage} TEXT NOT NULL DEFAULT ''",
                $"{MsgPayloadId} TEXT NOT NULL",
                $"{TenantId} INT NOT NULL DEFAULT 0",
                $"{ConsumerGroup} TEXT NOT NULL",
                $"{TaskId} BIGINT NOT NULL DEFAULT 0",
                $"{TaskCreatedAt} BIGINT NOT NULL DEFAULT 0",
                $"{TaskTransactId} TEXT NOT NULL DEFAULT ''",
                $"{TaskLockExpiresOn} BIGINT NOT NULL DEFAULT 0",
                $"{ErrorId} BIGINT NOT NULL DEFAULT 0",
                $"{DeliveryCreatedAt} BIGINT NOT NULL DEFAULT 0"
            ];
        }
    }

    /// <summary>
    /// Error table fields
    /// </summary>
    public sealed class ErrorTable
    {
        public const string Suffix = "__error$";

        public int FillFactor { get; set; } = 100;

        public string TableName { get; set; } = $"{Defaults.DatabaseTableName}{Suffix}";

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string ErrorId { get; set; } = "error_id";
            public string ErrorType { get; set; } = "error_type";
            public string ErrorMessage { get; set; } = "error_message";
            public string ErrorCreatedAt { get; set; } = "error_created_at";

            public string[] All() =>
            [
                $"{ErrorId} BIGINT NOT NULL",
                $"{ErrorType} TEXT NOT NULL",
                $"{ErrorMessage} TEXT NOT NULL",
                $"{ErrorCreatedAt} BIGINT NOT NULL DEFAULT 0"
            ];
        }
    }

    /// <summary>
    /// Type table fields
    /// </summary>
    public sealed class TypeTable
    {
        public const string Suffix = "__type$";

        public string TableName { get; set; } = $"{Defaults.DatabaseTableName}{Suffix}";

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string TypeId { get; set; } = "type_id";
            public string TypeName { get; set; } = "type_name";

            public string[] All() =>
            [
                $"{TypeId} BIGINT NOT NULL",
                $"{TypeName} TEXT NOT NULL"
            ];
        }
    }

    /// <summary>
    /// Offset table fields
    /// </summary>
    public sealed class OffsetTable
    {
        public const string Suffix = "__offset$";

        public string TableName { get; set; } = $"{Defaults.DatabaseTableName}{Suffix}";

        public TableFields Fields { get; } = new();

        public sealed class TableFields
        {
            public string ConsumerGroup { get; set; } = "consumer_group";
            public string TenantId { get; set; } = "tenant_id";
            public string GroupOffset { get; set; } = "group_offset";
            public string GroupUpdatedAt { get; set; } = "group_updated_at";

            public string[] All() =>
            [
                $"{ConsumerGroup} TEXT",
                $"{TenantId} INT NOT NULL DEFAULT 0",
                $"{GroupOffset} UUID NOT NULL DEFAULT '{Guid.Empty}'",
                $"{GroupUpdatedAt} TIMESTAMP WITH TIME ZONE DEFAULT NOW()"
            ];
        }
    }
}
