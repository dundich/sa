using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql;

internal sealed class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    public string DatabaseSchemaName => settings.DatabaseSchemaName;
    public string DatabaseMsgTableName => settings.DatabaseMsgTableName;
    public string DatabaseDeliveryTableName => settings.DatabaseDeliveryTableName;
    public string DatabaseErrorTableName => settings.DatabaseErrorTableName;
    public string DatabaseOffsetTableName => settings.DatabaseOffsetTableName;

    public string DatabaseTaskTableName => settings.DatabaseTaskTableName;


    public readonly static string[] MsgFields =
    [
        // ulid
        "msg_id CHAR(26) NOT NULL",

        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_part TEXT NOT NULL",

        "msg_payload_id TEXT NOT NULL",
        "msg_payload_type BIGINT NOT NULL",
        "msg_payload BYTEA NOT NULL",
        "msg_payload_size INT NOT NULL",
        
        //// -- rw
        //"outbox_transact_id TEXT NOT NULL DEFAULT ''",
        // "msg_lock_expires_on BIGINT NOT NULL DEFAULT 0",

        //// -- delivery
        //"outbox_delivery_attempt int NOT NULL DEFAULT 0",
        //// --- copy last
        //"outbox_delivery_id CHAR(26) NOT NULL DEFAULT ''",
        //"outbox_delivery_error_id TEXT NOT NULL DEFAULT ''",
        //"outbox_delivery_status_code INT NOT NULL DEFAULT 0",
        //"outbox_delivery_status_message TEXT NOT NULL DEFAULT ''",
        //"outbox_delivery_created_at BIGINT NOT NULL DEFAULT 0"
    ];


    public readonly static string[] TaskQueueFields =
    [
        "task_id BIGSERIAL",
        "consumer_group TEXT NOT NULL",
        // rw
        "task_lock_expires_on BIGINT NOT NULL DEFAULT 0",

        // msg
        "msg_id CHAR(26) NOT NULL",
        "msg_part TEXT NOT NULL",
        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_payload_id TEXT NOT NULL",
        "msg_created_at  BIGINT NOT NULL DEFAULT 0",
        
        // -- delivery
        $"delivery_id CHAR(26) NOT NULL DEFAULT '{CachedSqlParamNames.EmptyOffset}'",
        "delivery_attempt int NOT NULL DEFAULT 0",

        "delivery_transact_id TEXT NOT NULL DEFAULT ''",

        "delivery_status_code INT NOT NULL DEFAULT 0",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        "delivery_created_at BIGINT NOT NULL DEFAULT 0",

        // -- ref to error
        "error_id TEXT NOT NULL DEFAULT ''",
    ];


    public readonly static string[] DeliveryFields =
    [

        "delivery_id CHAR(26) NOT NULL",
        "delivery_status_code INT NOT NULL DEFAULT 0",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        

        // copy
        "msg_id CHAR(26) NOT NULL",
        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_part TEXT NOT NULL",

        // copy
        "consumer_group TEXT NOT NULL",
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

    //CREATE INDEX IF NOT EXISTS ix_{DatabaseTableName}__payload_type 
    //    ON {GetQualifiedTableName()} (payload_type);
    //    WHERE (outbox_delivery_status_code < {DeliveryStatusCode.Ok} OR outbox_delivery_status_code BETWEEN 300 AND 499)
    //""";


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


    static readonly string s_InTaskProcessing = $"""
 (delivery_status_code < {DeliveryStatusCode.Ok} OR delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.Status499})
""";


    public string SqlLockAndSelect =
$"""
WITH next_task AS (
    SELECT msg_id FROM {settings.GetQualifiedTaskTableName()}
    WHERE
        tenant_id = {CachedSqlParamNames.TenantId}
        AND msg_part = {CachedSqlParamNames.MsgPart}
        AND msg_payload_type = {CachedSqlParamNames.MsgPayloadType}
        
        AND msg_created_at >= {CachedSqlParamNames.FromDate}
        AND {s_InTaskProcessing}
        AND task_lock_expires_on < {CachedSqlParamNames.NowDate}
    LIMIT {CachedSqlParamNames.Limit}
    FOR UPDATE SKIP LOCKED
)
UPDATE {settings.GetQualifiedTaskTableName()}
SET
    delivery_status_code = CASE 
        WHEN delivery_status_code = 0 
            THEN {DeliveryStatusCode.Processing}
        ELSE delivery_status_code
    END
    ,task_transact_id = {CachedSqlParamNames.DeliveryTransactId}
    ,task_lock_expires_on = {CachedSqlParamNames.LockExpiresOn}
FROM 
    next_task
WHERE 
    {settings.GetQualifiedTaskTableName()}.msg_id = next_task.msg_id
RETURNING 
    {settings.GetQualifiedTaskTableName()}.msg_id
    ,tenant_id
    ,msg_part
    ,msg_payload
    ,msg_payload_id

    ,delivery_id
    ,delivery_attempt
    ,delivery_status_code
    ,delivery_status_message
    ,delivery_created_at

    ,error_id

    ,task_created_at
;
""";


    private static string SqlFinishDelivery(PgOutboxTableSettings settings, int count)
    {
        return
$"""
WITH inserted_delivery AS (
    INSERT INTO {settings.GetQualifiedDeliveryTableName()} (
        delivery_id
        , delivery_status_code
        , delivery_status_message        
        , delivery_transact_id
        , delivery_created_at

        , msg_id
        , tenant_id
        , msg_part

        , task_lock_expires_on

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
    , task.delivery_attempt = delivery_attempt 
        + CASE 
            WHEN inserted_delivery.delivery_status_code <> {DeliveryStatusCode.Postpone} 
                THEN 1
                ELSE 0
          END
    , task.error_id = inserted_delivery.derror_id

    , task.delivery_status_code = inserted_delivery.delivery_status_code
    , task.delivery_status_message = inserted_delivery.delivery_status_message
    , task.delivery_created_at = inserted_delivery.delivery_created_at

    , task.task_lock_expires_on = inserted_delivery.task_lock_expires_on
FROM 
    inserted_delivery
WHERE 
    tenant_id = {CachedSqlParamNames.TenantId}
    AND msg_part = {CachedSqlParamNames.MsgPart}
    AND msg_id = inserted_delivery.delivery_msg_id

    AND task_created_at >= {CachedSqlParamNames.FromDate}
    AND delivery_transact_id = {CachedSqlParamNames.DeliveryTransactId}
;

""";
    }


    private static string BuildDeliveryInsertValues(int count)
    {
        List<string> values = [];
        for (int i = 0; i < count; i++)
        {
            values.Add($"   (@id_{i},@st_{i},@msg_{i},@tid,@cr_{i},@msgid_{i},@tnt,@prt,@exp_{i},@err_{i})");
        }
        return string.Join(",\r\n", values);
    }


    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET 
    task_lock_expires_on = {CachedSqlParamNames.LockExpiresOn}
WHERE
    tenant_id = {CachedSqlParamNames.TenantId} 
    AND msg_part = {CachedSqlParamNames.MsgPart}
    AND msg_payload_type = {CachedSqlParamNames.MsgPayloadType}
    AND msg_created_at >= {CachedSqlParamNames.FromDate}

    AND {s_InTaskProcessing}
    AND delivery_transact_id = {CachedSqlParamNames.DeliveryTransactId}
    AND task_lock_expires_on > {CachedSqlParamNames.NowDate}

FOR UPDATE SKIP LOCKED
;
""";


    public string SqlFinishDelivery(int count) => SqlFinishDelivery(settings, count);


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
    ({CachedSqlParamNames.TypeId},{CachedSqlParamNames.TypeName})
ON CONFLICT DO NOTHING
;
""";


    private static string BuildErrorInsertValues(int count)
    {
        List<string> values = [];
        for (int i = 0; i < count; i++)
        {
            values.Add($"   (@id_{i},@st_{i},@msg_{i},@cr_{i})");
        }
        return string.Join(",\r\n", values);
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


    public string SqlCreateOffsetTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedOffsetTableName()}
(
    consumer_group TEXT,
    tenant_id INT NOT NULL DEFAULT 0,
    group_offset CHAR(26) NOT NULL DEFAULT '{CachedSqlParamNames.EmptyOffset}',
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
    ({CachedSqlParamNames.ConsumerGroupId},{CachedSqlParamNames.TenantId},{CachedSqlParamNames.GroupOffset})
ON CONFLICT (consumer_group, tenant_id) DO NOTHING;
;
""";


    public string SqlSelectOffset =
$"""
SELECT consumer_group 
FROM {settings.GetQualifiedOffsetTableName()} 
WHERE 
    consumer_group = {CachedSqlParamNames.ConsumerGroupId} 
    AND tenant_id = {CachedSqlParamNames.TenantId}
;
""";


    public string SqlUpdateOffset = $"""
UPDATE {settings.GetQualifiedOffsetTableName()}
SET 
    group_offset = {CachedSqlParamNames.GroupOffset}, 
    group_updated_at = NOW()
WHERE 
    consumer_group = {CachedSqlParamNames.ConsumerGroupId}
    AND tenant_id = {CachedSqlParamNames.TenantId}
;
""";


    public string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
    (consumer_group, tenant_id, group_offset)
VALUES 
    ({CachedSqlParamNames.ConsumerGroupId},{CachedSqlParamNames.TenantId},'{CachedSqlParamNames.EmptyOffset}')
ON CONFLICT (consumer_group, tenant_id) DO NOTHING
;
""";


    public string SqlLockOffset = $"SELECT pg_advisory_xact_lock({CachedSqlParamNames.OffsetKey});";


    public string SqlLoadConsumerGroup = $"""
WITH inserted_rows AS(
    INSERT INTO {settings.GetQualifiedTaskTableName()}
        (consumer_group,task_created_at,msg_id,msg_part,tenant_id,msg_payload_id,msg_created_at)
    SELECT
        {CachedSqlParamNames.ConsumerGroupId}
        ,{CachedSqlParamNames.NowDate}
        ,msg_id
        ,{CachedSqlParamNames.MsgPart}
        ,{CachedSqlParamNames.TenantId}
        ,msg_payload_id
        ,msg_created_at
    FROM {settings.GetQualifiedMsgTableName()}
    WHERE
        msg_part = {CachedSqlParamNames.MsgPart}
        AND tenant_id = {CachedSqlParamNames.TenantId}
        AND msg_created_at >= {CachedSqlParamNames.FromDate}
        AND msg_id > {CachedSqlParamNames.GroupOffset}
    ORDER BY msg_id
    LIMIT {CachedSqlParamNames.Limit}
    RETURNING msg_id
)
SELECT
    COUNT(*) as copied_rows,
    MAX(msg_id) as max_id
FROM inserted_rows
;
""";
}


internal sealed class CachedSqlParamNames : INamePrefixProvider
{

    public const string EmptyOffset = "01KBQ8DYRBSQ11R20ZKRBYD2G9";

    public const string TenantId = "@tenant";
    public const string GroupOffset = "@group_offset";
    public const string ConsumerGroupId = "@consumer_group";
    public const string MsgPart = "@part";
    public const string TypeId = "@type_id";
    public const string TypeName = "@type_name";
    public const string MsgPayloadType = "@payload_type";
    public const string FromDate = "@from_date";
    public const string DeliveryTransactId = "@transact_id";
    public const string NowDate = "@now";
    public const string OffsetKey = "@key";
    public const string Limit = "@limit";
    public const string LockExpiresOn = "@lock_expires_on";

    public static int MaxIndex => 512;

    public static string[] GetPrefixes() =>
    [
        "@id_",
        "@msgid_",
        "@err_",
        "@st_",
        "@msg_",
        "@exp_",
        "@cr_"
    ];
}
