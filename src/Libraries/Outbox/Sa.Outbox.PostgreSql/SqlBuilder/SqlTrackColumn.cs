namespace Sa.Outbox.PostgreSql.SqlBuilder;

/// <summary>
/// Column aliases of the tracking projection in <see cref="SqlOutboxBuilder.SqlTrackMessagesByPayload"/>.
/// </summary>
/// <remarks>
/// The aliases exist because <c>msg_id</c>, <c>tenant_id</c>, <c>msg_part</c> and <c>msg_created_at</c>
/// are named the same in the message and task tables: an unaliased join produces duplicate column
/// names, which makes name-based access ambiguous. The reader also relies on the projection order,
/// which these constants document.
/// </remarks>
internal static class SqlTrackColumn
{
    public const string MessageId = "m_id";
    public const string MsgSeq = "m_seq";
    public const string PayloadId = "m_payload_id";
    public const string TenantId = "m_tenant";
    public const string Part = "m_part";
    public const string CreatedAt = "m_created";

    public const string ConsumerGroup = "t_group";
    public const string TaskId = "t_task_id";
    public const string StatusCode = "t_status";
    public const string StatusMessage = "t_message";
    public const string Attempt = "t_attempt";
    public const string TaskCreatedAt = "t_task_created";
    public const string DeliveredAt = "t_delivered";
    public const string LockExpiresOn = "t_lock";
    public const string ErrorId = "t_error";
}
