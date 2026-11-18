namespace Sa.Outbox.PostgreSql.Configuration;

/// <summary>Default column names the PostgreSQL provider generates its SQL from.</summary>
public static class OutboxFieldDefaults
{
    // Common fields
    /// <summary>Message id column.</summary>
    public const string MsgId = "msg_id";
    /// <summary>Database-assigned insertion order of a message. The consumption cursor.</summary>
    public const string MsgSeq = "msg_seq";
    /// <summary>Tenant id column.</summary>
    public const string TenantId = "tenant_id";
    /// <summary>Partition key column.</summary>
    public const string MsgPart = "msg_part";
    /// <summary>Caller-supplied payload id column.</summary>
    public const string MsgPayloadId = "msg_payload_id";
    /// <summary>Payload type name column.</summary>
    public const string MsgPayloadType = "msg_payload_type";
    /// <summary>Serialized payload column.</summary>
    public const string MsgPayload = "msg_payload";
    /// <summary>Serialized payload size column.</summary>
    public const string MsgPayloadSize = "msg_payload_size";
    /// <summary>Message creation timestamp column.</summary>
    public const string MsgCreatedAt = "msg_created_at";

    // TaskQueue  outbox
    /// <summary>Task-queue row id column.</summary>
    public const string TaskId = "task_id";
    /// <summary>Consumer group column.</summary>
    public const string ConsumerGroup = "consumer_group";
    /// <summary>Task lock expiry column.</summary>
    public const string TaskLockExpiresOn = "task_lock_expires_on";
    /// <summary>Owning transaction column.</summary>
    public const string TaskTransactId = "task_transact_id";
    /// <summary>Task creation timestamp column.</summary>
    public const string TaskCreatedAt = "task_created_at";

    // Delivery  __log$
    /// <summary>Delivery log id column.</summary>
    public const string DeliveryId = "delivery_id";
    /// <summary>Delivery attempt counter column.</summary>
    public const string DeliveryAttempt = "delivery_attempt";
    /// <summary>Delivery status code column.</summary>
    public const string DeliveryStatusCode = "delivery_status_code";
    /// <summary>Delivery status message column.</summary>
    public const string DeliveryStatusMessage = "delivery_status_message";
    /// <summary>Delivery log timestamp column.</summary>
    public const string DeliveryCreatedAt = "delivery_created_at";

    // Error  __error$
    /// <summary>Error log id column.</summary>
    public const string ErrorId = "error_id";
    /// <summary>Error type column.</summary>
    public const string ErrorType = "error_type";
    /// <summary>Error message column.</summary>
    public const string ErrorMessage = "error_message";
    /// <summary>Error log timestamp column.</summary>
    public const string ErrorCreatedAt = "error_created_at";

    // Type  __type$
    /// <summary>Type registry id column.</summary>
    public const string TypeId = "type_id";
    /// <summary>Type registry name column.</summary>
    public const string TypeName = "type_name";

    // Offset  __offset
    /// <summary>Consumer group offset column.</summary>
    public const string GroupOffset = "group_offset";
    /// <summary>Msg-seq cursor column. Sits next to the legacy <see cref="GroupOffset"/> (UUID),
    /// which stays for rolling upgrades and is dropped by a later contract version.</summary>
    public const string GroupOffsetSeq = "group_offset_seq";
    /// <summary>Consumer group timestamp column.</summary>
    public const string GroupUpdatedAt = "group_updated_at";
}
