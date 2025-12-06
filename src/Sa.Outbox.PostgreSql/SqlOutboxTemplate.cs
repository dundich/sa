using Sa.Data.PostgreSql;

namespace Sa.Outbox.PostgreSql;

internal sealed class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    public string DatabaseSchemaName => settings.DatabaseSchemaName;
    public string DatabaseOutboxTableName => settings.DatabaseMsgTableName;
    public string DatabaseDeliveryTableName => settings.DatabaseDeliveryTableName;
    public string DatabaseErrorTableName => settings.DatabaseErrorTableName;
    public string DatabaseGroupTableName => settings.DatabaseGroupTableName;

    public string DatabaseTaskTableName => settings.DatabaseTaskTableName;


    public readonly static string[] OutboxFields =
    [
        // ulid
        "msg_id CHAR(26) NOT NULL",

        "msg_tenant INT NOT NULL DEFAULT 0",
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


    public readonly static string[] TaskFields =
    [
        "task_id BIGSERIAL PRIMARY KEY",
        "task_group_id TEXT NOT NULL",

        // msg
        "msg_id CHAR(26) NOT NULL",
        "msg_part TEXT NOT NULL",
        "msg_tenant INT NOT NULL DEFAULT 0",
        "msg_payload_id TEXT NOT NULL",
        "msg_created_at  BIGINT NOT NULL DEFAULT 0",
        
        // -- delivery
        "delivery_id CHAR(26) NOT NULL DEFAULT ''",
        "delivery_attempt int NOT NULL DEFAULT 0",

        "delivery_transact_id TEXT NOT NULL DEFAULT ''",
        "delivery_lock_expires_on BIGINT NOT NULL DEFAULT 0",

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
        "delivery_lock_expires_on BIGINT NOT NULL DEFAULT 0",

        // copy
        "msg_id CHAR(26) NOT NULL",
        "msg_tenant INT NOT NULL DEFAULT 0",
        "msg_part TEXT NOT NULL",

        // copy
        "task_group_id TEXT NOT NULL",
        "task_transact_id TEXT NOT NULL DEFAULT ''",
        

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
    ,msg_tenant
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


    static readonly string s_InTaskProcessing = @$"
(delivery_status_code < {DeliveryStatusCode.Ok} 
 OR delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.Status499})";


    public string SqlLockAndSelect =
$"""
WITH next_task AS (
    SELECT msg_id FROM {settings.GetQualifiedTaskTableName()}
    WHERE
        msg_tenant = @tenant
        AND msg_part = @part
        AND msg_payload_type = @payload_type
        
        AND msg_created_at >= @from_date
        AND {s_InTaskProcessing}
        AND task_lock_expires_on < @now
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {settings.GetQualifiedTaskTableName()}
SET
    delivery_status_code = CASE 
        WHEN delivery_status_code = 0 THEN {DeliveryStatusCode.Processing}
        ELSE delivery_status_code
    END
    ,task_transact_id = @transact_id
    ,task_lock_expires_on = @lock_expires_on
FROM 
    next_task
WHERE 
    {settings.GetQualifiedTaskTableName()}.msg_id = next_task.msg_id
RETURNING 
    {settings.GetQualifiedTaskTableName()}.msg_id
    ,msg_tenant
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
        , delivery_lock_expires_on
        , delivery_transact_id
        , delivery_created_at

        , msg_id
        , msg_tenant
        , msg_part

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
    , task.delivery_attempt = delivery_attempt + CASE
        WHEN inserted_delivery.delivery_status_code <> {DeliveryStatusCode.Postpone} 
            THEN 1
            ELSE 0
        END
    , task.error_id = inserted_delivery.derror_id

    , task.delivery_status_code = inserted_delivery.delivery_status_code
    , task.delivery_status_message = inserted_delivery.delivery_status_message
    , task.delivery_created_at = inserted_delivery.delivery_created_at

    , task.task_lock_expires_on = inserted_delivery.delivery_lock_expires_on
FROM 
    inserted_delivery
WHERE 
    msg_tenant = @tnt
    AND msg_part = @prt
    AND msg_id = inserted_delivery.delivery_msg_id

    AND task_created_at >= @from
    AND task_transact_id = @tid
;

""";
    }


    private static string BuildDeliveryInsertValues(int count)
    {
        List<string> values = [];
        for (int i = 0; i < count; i++)
        {
            values.Add($"   (@id_{i},@st_{i},@msg_{i},@exp_{i},@tid,@cr_{i},@msgid_{i},@tnt,@prt,@err_{i})");
        }
        return string.Join(",\r\n", values);
    }


    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET 
    task_lock_expires_on = @lock_expires_on
WHERE
    msg_tenant = @tenant 
    AND msg_part = @part 
    AND msg_payload_type = @payload_type
    AND msg_created_at >= @from_date

    AND {s_InTaskProcessing}
    AND task_transact_id = @transact_id
    AND task_lock_expires_on > @now

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
    (@type_id,@type_name)
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
    group_id TEXT PRIMARY KEY,
    group_offset CHAR(26) NOT NULL,
    group_updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
    CONSTRAINT "pk_{settings.DatabaseGroupTableName}" PRIMARY KEY (group_id)
)
;
""";

    public string SqlInsertOffset =
$"""
INSERT INTO {settings.GetQualifiedOffsetTableName} 
    (group_id, group_offset)
VALUES 
    (@group_id, 0)
ON CONFLICT (group_id) DO NOTHING;
;
""";
}

internal sealed class CachedSqlParamNames : INamePrefixProvider
{
    public static int MaxIndex => 512;

    public static string[] GetPrefixes() =>
    [
        "@id_",
        "@oid_",
        "@err_",
        "@st_",
        "@msg_",
        "@exp_",
        "@cr_"
    ];
}
