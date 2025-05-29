namespace Sa.Outbox.PostgreSql;

internal class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    public string DatabaseSchemaName => settings.DatabaseSchemaName;
    public string DatabaseOutboxTableName => settings.DatabaseOutboxTableName;
    public string DatabaseDeliveryTableName => settings.DatabaseDeliveryTableName;
    public string DatabaseErrorTableName => settings.DatabaseErrorTableName;


    public readonly static string[] OutboxFields =
    [
        // ulid
        "outbox_id CHAR(26) NOT NULL",

        // -- parts + outbox_created_at
        "outbox_tenant INT NOT NULL DEFAULT 0",
        "outbox_part TEXT NOT NULL",

        "outbox_payload_id TEXT NOT NULL",
        "outbox_payload_type BIGINT NOT NULL",
        "outbox_payload BYTEA NOT NULL",
        "outbox_payload_size INT NOT NULL",
        
        // -- rw
        "outbox_transact_id TEXT NOT NULL DEFAULT ''",
        "outbox_lock_expires_on BIGINT NOT NULL DEFAULT 0",

        // -- delivery
        "outbox_delivery_attempt int NOT NULL DEFAULT 0",
        // --- copy last
        "outbox_delivery_id CHAR(26) NOT NULL DEFAULT ''",
        "outbox_delivery_error_id TEXT NOT NULL DEFAULT ''",
        "outbox_delivery_status_code INT NOT NULL DEFAULT 0",
        "outbox_delivery_status_message TEXT NOT NULL DEFAULT ''",
        "outbox_delivery_created_at BIGINT NOT NULL DEFAULT 0"
    ];


    public readonly static string[] DeliveryFields =
    [
        "delivery_id CHAR(26) NOT NULL",
        "delivery_outbox_id CHAR(26) NOT NULL",
        "delivery_error_id TEXT NOT NULL DEFAULT ''",
        "delivery_status_code INT NOT NULL DEFAULT 0",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        "delivery_transact_id TEXT NOT NULL DEFAULT ''",
        "delivery_lock_expires_on BIGINT NOT NULL DEFAULT 0",
        // - parts
        "delivery_tenant INT NOT NULL DEFAULT 0",
        "delivery_part TEXT NOT NULL",
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


    public string SqlBulkOutboxCopy =
$"""
COPY {settings.GetQualifiedOutboxTableName()} (
    outbox_id
    ,outbox_tenant
    ,outbox_part
    ,outbox_payload_id
    ,outbox_payload_type
    ,outbox_payload
    ,outbox_payload_size
    ,outbox_created_at
) 
FROM STDIN (FORMAT BINARY)
;
""";


    static readonly string s_InProcessing = $"(outbox_delivery_status_code < {DeliveryStatusCode.Ok} OR outbox_delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.Status499})";


    public string SqlLockAndSelect =
$"""
WITH next_task AS (
    SELECT outbox_id FROM {settings.GetQualifiedOutboxTableName()}
    WHERE
        outbox_tenant = @tenant AND outbox_part = @part AND outbox_created_at >= @from_date
        AND outbox_payload_type = @payload_type
        AND {s_InProcessing}
        AND outbox_lock_expires_on < @now
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE {settings.GetQualifiedOutboxTableName()}
SET
    outbox_delivery_status_code = CASE 
        WHEN outbox_delivery_status_code = 0 THEN {DeliveryStatusCode.Processing}
        ELSE outbox_delivery_status_code
    END
    ,outbox_transact_id = @transact_id
    ,outbox_lock_expires_on = @lock_expires_on
FROM 
    next_task
WHERE 
    {settings.GetQualifiedOutboxTableName()}.outbox_id = next_task.outbox_id
RETURNING 
    {settings.GetQualifiedOutboxTableName()}.outbox_id
    ,outbox_tenant
    ,outbox_part
    ,outbox_payload
    ,outbox_payload_id
    ,outbox_delivery_id
    ,outbox_delivery_attempt
    ,outbox_delivery_error_id
    ,outbox_delivery_status_code
    ,outbox_delivery_status_message
    ,outbox_delivery_created_at
    ,outbox_created_at
;
""";


    private static string SqlFinishDelivery(PgOutboxTableSettings settings, int count)
    {
        return
$"""
WITH inserted_delivery AS (
    INSERT INTO {settings.GetQualifiedDeliveryTableName()} (
        delivery_id
        , delivery_outbox_id
        , delivery_error_id
        , delivery_status_code
        , delivery_status_message
        , delivery_lock_expires_on
        , delivery_transact_id
        , delivery_tenant
        , delivery_part
        , delivery_created_at
    )
    VALUES
{BuildDeliveryInsertValues(count)}
    ON CONFLICT DO NOTHING
    RETURNING *
)
UPDATE {settings.GetQualifiedOutboxTableName()} 
SET 
    outbox_delivery_id = inserted_delivery.delivery_id
    , outbox_delivery_attempt = outbox_delivery_attempt + CASE 
        WHEN inserted_delivery.delivery_status_code <> {DeliveryStatusCode.Postpone} THEN 1 
        ELSE 0 
      END
    , outbox_delivery_error_id = inserted_delivery.delivery_error_id
    , outbox_delivery_status_code = inserted_delivery.delivery_status_code
    , outbox_delivery_status_message = inserted_delivery.delivery_status_message
    , outbox_lock_expires_on = inserted_delivery.delivery_lock_expires_on
    , outbox_delivery_created_at = inserted_delivery.delivery_created_at
FROM 
    inserted_delivery
WHERE 
    outbox_tenant = @tnt 
    AND outbox_part = @prt 
    AND outbox_created_at >= @from_date
    AND outbox_transact_id = @tid
    AND outbox_id = inserted_delivery.delivery_outbox_id
;

""";
    }

    private static string BuildDeliveryInsertValues(int count)
    {
        List<string> values = [];
        int j = 0;
        for (int i = 0; i < count; i++)
        {
            // @id_{i},@outbox_id_{i},@error_id_{i},@status_code_{i},@status_message_{i},@lock_expires_on_{i},@created_at_{i}
            values.Add($"   (@p{j++},@p{j++},@p{j++},@p{j++},@p{j++},@p{j++},@tid,@tnt,@prt,@p{j++})");
        }
        return string.Join(",\r\n", values);
    }


    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedOutboxTableName()}
SET 
    outbox_lock_expires_on = @lock_expires_on
WHERE
    outbox_tenant = @tenant AND outbox_part = @part AND outbox_created_at >= @from_date
    AND outbox_payload_type = @payload_type
    AND {s_InProcessing}
    AND outbox_transact_id = @transact_id
    AND outbox_lock_expires_on > @now
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
            values.Add($"   (@id_{i},@type_{i},@message_{i},@created_at_{i})");
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
}
