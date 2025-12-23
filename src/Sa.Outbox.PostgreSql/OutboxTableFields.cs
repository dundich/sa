namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Provides table field definitions for PostgreSQL outbox tables.
/// </summary>
internal static class OutboxTableFields
{
    /// <summary>
    /// Message table fields (read-only table)
    /// </summary>
    public static class Message
    {
        public const string MsgId = "msg_id";
        public const string TenantId = "tenant_id";
        public const string MsgPart = "msg_part";
        public const string MsgPayloadId = "msg_payload_id";
        public const string MsgPayloadType = "msg_payload_type";
        public const string MsgPayload = "msg_payload";
        public const string MsgPayloadSize = "msg_payload_size";
        public const string MsgCreatedAt = "msg_created_at";

        public static readonly string[] All =
        [
            $"{MsgId} UUID NOT NULL",
            $"{TenantId} INT NOT NULL DEFAULT 0",
            $"{MsgPart} TEXT NOT NULL",
            $"{MsgPayloadId} TEXT NOT NULL DEFAULT ''",
            $"{MsgPayloadType} BIGINT NOT NULL",
            $"{MsgPayload} BYTEA NOT NULL",
            $"{MsgPayloadSize} INT NOT NULL",
            $"{MsgCreatedAt} BIGINT NOT NULL DEFAULT 0"
        ];
    }

    /// <summary>
    /// Task queue table fields (read-write table)
    /// </summary>
    public static class TaskQueue
    {
        public const string TaskId = "task_id";
        public const string ConsumerGroup = "consumer_group";
        public const string TaskLockExpiresOn = "task_lock_expires_on";
        public const string TaskTransactId = "task_transact_id";

        // Message references
        public const string MsgId = "msg_id";
        public const string MsgPart = "msg_part";
        public const string TenantId = "tenant_id";
        public const string MsgPayloadId = "msg_payload_id";
        public const string MsgCreatedAt = "msg_created_at";

        // Delivery information
        public const string DeliveryId = "delivery_id";
        public const string DeliveryAttempt = "delivery_attempt";
        public const string DeliveryStatusCode = "delivery_status_code";
        public const string DeliveryStatusMessage = "delivery_status_message";
        public const string DeliveryCreatedAt = "delivery_created_at";

        // Error reference
        public const string ErrorId = "error_id";
        public const string TaskCreatedAt = "task_created_at";

        public static readonly string[] All =
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

    /// <summary>
    /// Delivery table fields (read-only table)
    /// </summary>
    public static class Delivery
    {
        public const string DeliveryId = "delivery_id";
        public const string DeliveryStatusCode = "delivery_status_code";
        public const string DeliveryStatusMessage = "delivery_status_message";
        public const string MsgPayloadId = "msg_payload_id";
        public const string TenantId = "tenant_id";
        public const string ConsumerGroup = "consumer_group";
        public const string TaskId = "task_id";
        public const string TaskCreatedAt = "task_created_at";
        public const string TaskTransactId = "task_transact_id";
        public const string TaskLockExpiresOn = "task_lock_expires_on";
        public const string ErrorId = "error_id";
        public const string DeliveryCreatedAt = "delivery_created_at";

        public static readonly string[] All =
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

    /// <summary>
    /// Error table fields
    /// </summary>
    public static class Error
    {
        public const string ErrorId = "error_id";
        public const string ErrorType = "error_type";
        public const string ErrorMessage = "error_message";
        public const string ErrorCreatedAt = "error_created_at";

        public static readonly string[] All =
        [
            $"{ErrorId} BIGINT NOT NULL",
            $"{ErrorType} TEXT NOT NULL",
            $"{ErrorMessage} TEXT NOT NULL",
            $"{ErrorCreatedAt} BIGINT NOT NULL DEFAULT 0"
        ];
    }

    /// <summary>
    /// Type table fields
    /// </summary>
    public static class TypeTable
    {
        public const string TypeId = "type_id";
        public const string TypeName = "type_name";

        public static readonly string[] All =
        [
            $"{TypeId} BIGINT NOT NULL",
            $"{TypeName} TEXT NOT NULL"
        ];
    }

    /// <summary>
    /// Offset table fields
    /// </summary>
    public static class Offset
    {
        public const string ConsumerGroup = "consumer_group";
        public const string TenantId = "tenant_id";
        public const string GroupOffset = "group_offset";
        public const string GroupUpdatedAt = "group_updated_at";

        public static readonly string[] All =
        [
            $"{ConsumerGroup} TEXT",
            $"{TenantId} INT NOT NULL DEFAULT 0",
            $"{GroupOffset} UUID NOT NULL DEFAULT '{Guid.Empty}'",
            $"{GroupUpdatedAt} TIMESTAMP WITH TIME ZONE DEFAULT NOW()"
        ];
    }
}
