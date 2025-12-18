using System.Text;
using Sa.Outbox.PostgreSql.Repository;

namespace Sa.Outbox.PostgreSql;

internal sealed class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    public string DatabaseSchemaName => settings.DatabaseSchemaName;
    public string DatabaseMsgTableName => settings.DatabaseMsgTableName;
    public string DatabaseDeliveryTableName => settings.DatabaseDeliveryTableName;
    public string DatabaseErrorTableName => settings.DatabaseErrorTableName;
    public string DatabaseOffsetTableName => settings.DatabaseOffsetTableName;

    public string DatabaseTaskTableName => settings.DatabaseTaskTableName;

    // ro
    public readonly static string[] MsgFields =
    [
        // ulid
        "msg_id CHAR(26) NOT NULL",

        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_part TEXT NOT NULL",

        "msg_payload_id TEXT NOT NULL DEFAULT ''",
        "msg_payload_type BIGINT NOT NULL",
        "msg_payload BYTEA NOT NULL",
        "msg_payload_size INT NOT NULL"
    ];


    // rw
    public readonly static string[] TaskQueueFields =
    [
        "task_id BIGSERIAL NOT NULL",
        "consumer_group TEXT NOT NULL",
        // rw
        "task_lock_expires_on BIGINT NOT NULL DEFAULT 0",
        "task_transact_id TEXT NOT NULL DEFAULT ''",

        // msg
        "msg_id CHAR(26) NOT NULL",
        "msg_part TEXT NOT NULL",
        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_payload_id TEXT NOT NULL",
        "msg_created_at BIGINT NOT NULL DEFAULT 0",
        
        // -- delivery
        $"delivery_id BIGINT NOT NULL DEFAULT 0",
        "delivery_attempt int NOT NULL DEFAULT 0",

        "delivery_status_code INT NOT NULL DEFAULT 0",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        "delivery_created_at BIGINT NOT NULL DEFAULT 0",

        // -- ref to error
        "error_id TEXT NOT NULL DEFAULT ''",
    ];

    // ro
    public readonly static string[] DeliveryFields =
    [
        "delivery_id BIGSERIAL NOT NULL",
        "delivery_status_code INT NOT NULL DEFAULT 0",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        
        // copy
        "msg_payload_id TEXT NOT NULL",
        "tenant_id INT NOT NULL DEFAULT 0",

        // copy
        "task_id BIGINT NOT NULL DEFAULT 0",
        "consumer_group TEXT NOT NULL",
        "task_created_at BIGINT NOT NULL DEFAULT 0",

        "task_transact_id TEXT NOT NULL DEFAULT ''",
        "task_lock_expires_on BIGINT NOT NULL DEFAULT 0",

        // ref
        "error_id TEXT NOT NULL DEFAULT ''",
    ];

    // Delivery errors
    public readonly static string[] ErrorFields =
    [
        "error_id BIGINT NOT NULL",
        "error_type TEXT NOT NULL",
        "error_message TEXT NOT NULL",
    ];


    //CREATE INDEX IF NOT EXISTS ix_{DatabaseTableName}__payload_id 
    //    ON {GetQualifiedTableName()} (payload_id) 
    //    WHERE payload_id <> '' AND (outbox_delivery_status_code BETWEEN {DeliveryStatusCode.Ok} AND 299 OR outbox_delivery_status_code >= 500)


    public string SqlBulkMsgCopy =
$"""
COPY {settings.GetQualifiedMsgTableName()} (
    msg_id
    ,tenant_id
    ,msg_part
    ,msg_payload_id
    ,msg_payload_type
    ,msg_payload
    ,msg_payload_size
    ,msg_created_at
)
FROM STDIN (FORMAT BINARY)
;
""";


    static readonly string s_InTaskProcessing = $"(delivery_status_code < {DeliveryStatusCode.Ok} OR delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.Status499})";


    public string SqlLockAndSelect =
$"""
WITH next_task AS (
    SELECT 
        task.task_id,
        task.tenant_id,
        task.consumer_group,
        msg.msg_id,
        msg.msg_part,
        msg.msg_payload,
        msg.msg_payload_id,
        msg.msg_payload_type,
        msg.msg_created_at,
        task.delivery_id,
        task.delivery_attempt,
        task.delivery_status_code,
        task.delivery_status_message,
        task.delivery_created_at,
        task.error_id,
        task.task_created_at
    FROM {settings.GetQualifiedTaskTableName()} task
    INNER JOIN {settings.GetQualifiedMsgTableName()} msg 
        ON task.msg_id = msg.msg_id
    WHERE
        task.tenant_id = {SqlParam.TenantId}
        AND task.consumer_group = {SqlParam.ConsumerGroupId}
        AND task.task_created_at >= {SqlParam.FromDate}
        AND {s_InTaskProcessing}
        AND task.task_lock_expires_on < {SqlParam.ToDate}
        AND msg.msg_part = {SqlParam.MsgPart}
        AND msg.tenant_id = {SqlParam.TenantId}
        AND msg.msg_created_at >= {SqlParam.FromDate}
        AND msg.msg_payload_type = {SqlParam.MsgPayloadType}
    ORDER BY task.task_id
    LIMIT {SqlParam.Limit}
    FOR UPDATE SKIP LOCKED
),
updated_tasks AS (
    UPDATE {settings.GetQualifiedTaskTableName()} t
    SET
        delivery_status_code = {DeliveryStatusCode.Processing},
        task_transact_id = {SqlParam.TransactId},
        task_lock_expires_on = {SqlParam.LockExpiresOn}
    FROM next_task nt
    WHERE 
        t.task_id = nt.task_id 
        AND t.tenant_id = {SqlParam.TenantId}
        AND t.consumer_group = {SqlParam.ConsumerGroupId} 
    RETURNING t.*
)
SELECT
    nt.task_id,
    nt.tenant_id,
    nt.consumer_group,
    nt.msg_id,
    nt.msg_part,
    nt.msg_payload_id,
    nt.msg_payload,
    nt.msg_payload_type,
    nt.msg_created_at,
    nt.delivery_id,
    nt.delivery_attempt,
    nt.delivery_status_code,
    nt.delivery_status_message,
    nt.delivery_created_at,
    nt.error_id,
    nt.task_created_at
FROM next_task nt
;

""";



    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET 
    task_lock_expires_on = {SqlParam.LockExpiresOn}
WHERE
    tenant_id = {SqlParam.TenantId}
    AND consumer_group = {SqlParam.ConsumerGroupId}
    AND task_created_at >= {SqlParam.FromDate}
    AND {s_InTaskProcessing}
    AND task_transact_id = {SqlParam.TransactId}
    AND task_lock_expires_on > {SqlParam.NowDate} -- now
;
""";


    public string SqlCreateTypeTable
    =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTypeTableName()}
(
    type_id BIGINT NOT NULL,
    type_name TEXT NOT NULL,
    CONSTRAINT "pk_{settings.DatabaseTypeTableName}" PRIMARY KEY (type_id)
)
;
""";


    public string SqlSelectType = $"SELECT * FROM {settings.GetQualifiedTypeTableName()}";

    public string SqlInsertType =
$"""
INSERT INTO {settings.GetQualifiedTypeTableName()} 
    (type_id, type_name)
VALUES
    ({SqlParam.TypeId},{SqlParam.TypeName})
ON CONFLICT DO NOTHING
;
""";


    public string SqlCreateOffsetTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedOffsetTableName()}
(
    consumer_group TEXT,
    tenant_id INT NOT NULL DEFAULT 0,
    group_offset CHAR(26) NOT NULL DEFAULT '{GroupOffset.Empty.OffsetId}',
    group_updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    CONSTRAINT "pk_{settings.DatabaseOffsetTableName}" PRIMARY KEY (consumer_group, tenant_id)
)
;
""";

    public string SqlInsertOffset =
$"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
    (consumer_group, tenant_id, group_offset)
VALUES 
    ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},{SqlParam.GroupOffset})
ON CONFLICT (consumer_group, tenant_id) DO NOTHING;
;
""";


    public string SqlSelectOffset =
$"""
SELECT group_offset 
FROM {settings.GetQualifiedOffsetTableName()} 
WHERE 
    consumer_group = {SqlParam.ConsumerGroupId} 
    AND tenant_id = {SqlParam.TenantId}
;
""";


    public string SqlUpdateOffset = $"""
UPDATE {settings.GetQualifiedOffsetTableName()}
SET 
    group_offset = {SqlParam.GroupOffset}, 
    group_updated_at = NOW()
WHERE 
    consumer_group = {SqlParam.ConsumerGroupId}
    AND tenant_id = {SqlParam.TenantId}
;
""";


    public string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
    (consumer_group, tenant_id, group_offset)
VALUES 
    ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},'{GroupOffset.Empty.OffsetId}')
ON CONFLICT (consumer_group, tenant_id) DO NOTHING
;
""";


    public string SqlLockOffset = $"SELECT pg_advisory_xact_lock({SqlParam.OffsetKey});";


    public string SqlLoadConsumerGroup = $"""
WITH inserted_rows AS(
    INSERT INTO {settings.GetQualifiedTaskTableName()}
        (consumer_group,task_created_at,msg_id,msg_part,tenant_id,msg_payload_id,msg_created_at)
    SELECT
        {SqlParam.ConsumerGroupId}
        ,{SqlParam.NowDate}
        ,msg_id
        ,{SqlParam.MsgPart}
        ,{SqlParam.TenantId}
        ,msg_payload_id
        ,msg_created_at
    FROM {settings.GetQualifiedMsgTableName()}
    WHERE
        msg_part = {SqlParam.MsgPart}
        AND tenant_id = {SqlParam.TenantId}
        AND msg_created_at >= {SqlParam.FromDate}
        AND msg_created_at <= {SqlParam.ToDate}
        AND msg_id > {SqlParam.GroupOffset}
    ORDER BY msg_id
    LIMIT {SqlParam.Limit}
    RETURNING msg_id
)
SELECT
    COUNT(*) as copied_rows,
    MAX(msg_id) as max_id
FROM inserted_rows
;
""";


    private static string BuildErrorInsertValues(int count)
    {
        const int estimatedLengthPerRow = 70;
        var sb = new StringBuilder(count * estimatedLengthPerRow + (count - 1) * 3);

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
                sb.Append('\n');
            }

            sb.Append('(')
              .Append(SqlParam.ErrorId).Append(i)
              .Append(',')
              .Append(SqlParam.TypeName).Append(i)
              .Append(',')
              .Append(SqlParam.StatusMessage).Append(i)
              .Append(',')
              .Append(SqlParam.CreatedAt).Append(i)
              .Append(')');
        }

        return sb.ToString();
    }

    private static string SqlError(PgOutboxTableSettings settings, int count) =>
$"""
INSERT INTO {settings.GetQualifiedErrorTableName()} 
    (error_id,error_type,error_message,error_created_at)
VALUES
{BuildErrorInsertValues(count)}
ON CONFLICT DO NOTHING
;
""";

    public string SqlError(int count) => SqlError(settings, count);




    public string SqlFinishDelivery(int count) => SqlFinishDelivery(settings, count);

    private static string SqlFinishDelivery(PgOutboxTableSettings settings, int count)
    {
        return
$"""
WITH inserted_delivery AS (
    INSERT INTO {settings.GetQualifiedDeliveryTableName()} (

        delivery_status_code
        , delivery_status_message
        , delivery_created_at

        , msg_payload_id

        , tenant_id
        , consumer_group

        , task_id
        , task_transact_id
        , task_lock_expires_on
        , task_created_at

        , error_id
    )
    VALUES
{BuildDeliveryInsertValues(count)}
    ON CONFLICT DO NOTHING
    RETURNING *
)
UPDATE {settings.GetQualifiedTaskTableName()} task
SET
    delivery_id = inserted_delivery.delivery_id
    , delivery_attempt = task.delivery_attempt
        + CASE 
            WHEN inserted_delivery.delivery_status_code <> {DeliveryStatusCode.Postpone} 
                THEN 1
                ELSE 0
          END
    , error_id = inserted_delivery.error_id

    , delivery_status_code = inserted_delivery.delivery_status_code
    , delivery_status_message = inserted_delivery.delivery_status_message
    , delivery_created_at = inserted_delivery.delivery_created_at

    , task_lock_expires_on = inserted_delivery.task_lock_expires_on
FROM 
    inserted_delivery
WHERE 
    task.tenant_id = {SqlParam.TenantId}
    AND task.consumer_group = {SqlParam.ConsumerGroupId}
    AND task.task_id = inserted_delivery.task_id

    AND task.task_created_at >= {SqlParam.FromDate}
    AND task.task_transact_id = {SqlParam.TransactId}
;

""";
    }



    private static string BuildDeliveryInsertValues(int count)
    {
        var estimatedLength = count * 150 + (count - 1) * 3;
        var sb = new StringBuilder(estimatedLength);

        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(",\n");

            sb.Append('(')
              .Append(SqlParam.StatusCode).Append(i).Append(',')
              .Append(SqlParam.StatusMessage).Append(i).Append(',')
              .Append(SqlParam.CreatedAt).Append(i).Append(',')
              .Append(SqlParam.PayloadId).Append(i).Append(',')
              .Append(SqlParam.TenantId).Append(',')
              .Append(SqlParam.ConsumerGroupId).Append(',')
              .Append(SqlParam.TaskId).Append(i).Append(',')
              .Append(SqlParam.TransactId).Append(',')
              .Append(SqlParam.LockExpiresOn).Append(i).Append(',')
              .Append(SqlParam.TaskCreatedAt).Append(i).Append(',')
              .Append(SqlParam.ErrorId).Append(i)
              .Append(')');
        }

        return sb.ToString();
    }
}

internal static class SqlParam
{
    public const string TenantId = "@tnt";
    public const string GroupOffset = "@offset";
    public const string ConsumerGroupId = "@gr";
    public const string MsgPart = "@part";
    public const string TypeId = "@type";
    public const string TypeName = "@type_name";
    public const string MsgPayloadType = "@p_type";
    public const string FromDate = "@from_date";
    public const string ToDate = "@to_date";
    public const string NowDate = "@now";
    public const string TransactId = "@trn";
    public const string OffsetKey = "@key";
    public const string Limit = "@limit";
    public const string LockExpiresOn = "@lock";

    public const string PayloadId = "@p_id";
    public const string TaskId = "@tsk";
    public const string StatusCode = "@code";
    public const string StatusMessage = "@msg";
    public const string CreatedAt = "@cr";
    public const string TaskCreatedAt = "@tcr";
    public const string ErrorId = "@err_id";
}
