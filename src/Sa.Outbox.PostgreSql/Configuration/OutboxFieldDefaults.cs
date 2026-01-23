namespace Sa.Outbox.PostgreSql;

public static class OutboxFieldDefaults
{
    // Common fields
    public const string MsgId = "msg_id";
    public const string TenantId = "tenant_id";
    public const string MsgPart = "msg_part";
    public const string MsgPayloadId = "msg_payload_id";
    public const string MsgPayloadType = "msg_payload_type";
    public const string MsgPayload = "msg_payload";
    public const string MsgPayloadSize = "msg_payload_size";
    public const string MsgCreatedAt = "msg_created_at";

    // TaskQueue  outbox
    public const string TaskId = "task_id";
    public const string ConsumerGroup = "consumer_group";
    public const string TaskLockExpiresOn = "task_lock_expires_on";
    public const string TaskTransactId = "task_transact_id";
    public const string TaskCreatedAt = "task_created_at";

    // Delivery  __log$
    public const string DeliveryId = "delivery_id";
    public const string DeliveryAttempt = "delivery_attempt";
    public const string DeliveryStatusCode = "delivery_status_code";
    public const string DeliveryStatusMessage = "delivery_status_message";
    public const string DeliveryCreatedAt = "delivery_created_at";

    // Error  __error$
    public const string ErrorId = "error_id";
    public const string ErrorType = "error_type";
    public const string ErrorMessage = "error_message";
    public const string ErrorCreatedAt = "error_created_at";

    // Type  __type$
    public const string TypeId = "type_id";
    public const string TypeName = "type_name";

    // Offset  __offset
    public const string GroupOffset = "group_offset";
    public const string GroupUpdatedAt = "group_updated_at";
}
