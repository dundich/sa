using System.Data;
using System.Text;
using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Extensions;

namespace Sa.Outbox.PostgreSql;

internal sealed class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    public string DatabaseSchemaName => settings.DatabaseSchemaName;
    public string DatabaseMsgTableName => settings.DatabaseMsgTableName;
    public string DatabaseDeliveryTableName => settings.DatabaseDeliveryTableName;
    public string DatabaseErrorTableName => settings.DatabaseErrorTableName;
    public string DatabaseOffsetTableName => settings.DatabaseOffsetTableName;

    public string DatabaseTaskTableName => settings.DatabaseTableName;

    // ro
    public readonly static string[] MsgFields =
    [
        // ulid
        "msg_id UUID NOT NULL",

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
        "msg_id UUID NOT NULL",
        "msg_part TEXT NOT NULL",
        "tenant_id INT NOT NULL DEFAULT 0",
        "msg_payload_id TEXT NOT NULL",
        "msg_created_at BIGINT NOT NULL DEFAULT 0",
        
        // -- delivery
        "delivery_id BIGINT NOT NULL DEFAULT 0",
        "delivery_attempt int NOT NULL DEFAULT 0",

        $"delivery_status_code INT NOT NULL DEFAULT {DeliveryStatusCode.Pending}",
        "delivery_status_message TEXT NOT NULL DEFAULT ''",
        "delivery_created_at BIGINT NOT NULL DEFAULT 0",

        // -- ref to error
        "error_id BIGINT NOT NULL DEFAULT 0",
    ];

    // ro
    public readonly static string[] DeliveryFields =
    [
        "delivery_id BIGSERIAL NOT NULL",
        $"delivery_status_code INT NOT NULL DEFAULT {DeliveryStatusCode.Pending}",
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
        "error_id BIGINT NOT NULL DEFAULT 0",
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


    static readonly string s_InTaskProcessing = $"(delivery_status_code < {DeliveryStatusCode.Ok} OR delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.WarnEof})";


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
        AND msg.msg_payload_type = {SqlParam.TypeId}
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
    AND msg_payload_type = {SqlParam.TypeId}
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
    group_offset UUID NOT NULL DEFAULT '{Guid.Empty}',
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
    ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},{SqlParam.Offset})
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
    group_offset = {SqlParam.Offset}, 
    group_updated_at = NOW()
WHERE 
    consumer_group = {SqlParam.ConsumerGroupId}
    AND tenant_id = {SqlParam.TenantId}
;
""";


    public string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
    (consumer_group, tenant_id)
VALUES 
    ({SqlParam.ConsumerGroupId},{SqlParam.TenantId})
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
        AND msg_id > {SqlParam.Offset}
    ORDER BY msg_id
    LIMIT {SqlParam.Limit}
    RETURNING msg_id
)
SELECT
    COUNT(*) as copied_rows,
    (SELECT msg_id FROM inserted_rows ORDER BY msg_id DESC LIMIT 1) as max_id
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

internal static class NpgsqlDataReaderExtension
{
    public static Guid GetMgsId(this NpgsqlDataReader reader)
        => reader.GetGuid("msg_id");

    public static string GetMgsPayloadId(this NpgsqlDataReader reader)
        => reader.GetString("msg_payload_id");

    public static Stream GetMgsPayload(this NpgsqlDataReader reader)
        => reader.GetStream("msg_payload");

    public static int GetTenantId(this NpgsqlDataReader reader)
        => reader.GetInt32("tenant_id");

    public static string GetMsgPart(this NpgsqlDataReader reader)
        => reader.GetString("msg_part");

    public static DateTimeOffset GetMsgCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64("msg_created_at").ToDateTimeOffsetFromUnixTimestamp();

    public static long GetTaskId(this NpgsqlDataReader reader)
        => reader.GetInt64("task_id");

    public static DateTimeOffset GetTaskCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64("task_created_at").ToDateTimeOffsetFromUnixTimestamp();

    public static long GetDeliveryId(this NpgsqlDataReader reader)
        => reader.GetInt64("delivery_id");

    public static int GetDeliveryAttempt(this NpgsqlDataReader reader)
        => reader.GetInt32("delivery_attempt");

    public static long GetErrorId(this NpgsqlDataReader reader)
        => reader.GetInt64("error_id");

    public static string GetConsumerGroup(this NpgsqlDataReader reader)
        => reader.GetString("consumer_group");

    public static int GetDeliveryStatusCode(this NpgsqlDataReader reader)
    => reader.GetInt32("delivery_status_code");

    public static string GetDeliveryStatusMessage(this NpgsqlDataReader reader)
        => reader.GetString("delivery_status_message");

    public static long GetDeliveryCreatedAt(this NpgsqlDataReader reader)
        => reader.GetInt64("delivery_created_at");

    public static long GetTypeId(this NpgsqlDataReader reader)
        => reader.GetInt64("type_id");

    public static string GetTypeName(this NpgsqlDataReader reader)
        => reader.GetString("type_name");

}

internal static class SqlParam
{
    public const string TenantId = "@tnt";

    public const string ConsumerGroupId = "@gr";
    public const string MsgPart = "@prt";
    public const string TypeId = "@typ_id";
    public const string TypeName = "@typ_nm";

    public const string FromDate = "@frm";
    public const string ToDate = "@to";
    public const string NowDate = "@now";
    public const string TransactId = "@trn";

    public const string OffsetKey = "@off_key";
    public const string Offset = "@offset";

    public const string Limit = "@limit";
    public const string LockExpiresOn = "@lck";

    public const string PayloadId = "@pl_id";
    public const string TaskId = "@tsk";
    public const string StatusCode = "@st_cd";
    public const string StatusMessage = "@st_msg";
    public const string CreatedAt = "@cr_at";
    public const string TaskCreatedAt = "@tsk_at";
    public const string ErrorId = "@err_id";
}


internal static class NpgsqlCommandExtension
{
    public static NpgsqlCommand AddParamTenantId(this NpgsqlCommand command, int value)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, value));
        return command;
    }
    public static NpgsqlCommand AddParamMsgPart(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, value));
        return command;
    }

    public static NpgsqlCommand AddParamFromDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FromDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamToDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.ToDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamNowDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.NowDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamConsumerGroupId(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, value));
        return command;
    }

    public static NpgsqlCommand AddParamTypeId(this NpgsqlCommand command, long value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.TypeId, value));
        return command;
    }

    public static NpgsqlCommand AddParamTransactId(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TransactId, value));
        return command;
    }

    public static NpgsqlCommand AddParamLimit(this NpgsqlCommand command, int value)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, value));
        return command;
    }

    public static NpgsqlCommand AddParamLockExpiresOn(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.LockExpiresOn, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamLockExpiresOn(this NpgsqlCommand command, long value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.LockExpiresOn, value, index);


    public static NpgsqlCommand AddParamErrorId(this NpgsqlCommand command, long? value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.ErrorId, value ?? 0, index);


    public static NpgsqlCommand AddParamTypeName(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TypeName, value));
        return command;
    }

    public static NpgsqlCommand AddParamTypeName(this NpgsqlCommand command, string? value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.TypeName, value ?? string.Empty, index);

    public static NpgsqlCommand AddParamStatusCode(this NpgsqlCommand command, int value, int index)
        => command.AddParam<BatchParams, int>(SqlParam.StatusCode, value, index);

    public static NpgsqlCommand AddParamStatusMessage(this NpgsqlCommand command, string? value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.StatusMessage, value ?? string.Empty, index);

    public static NpgsqlCommand AddParamCreatedAt(this NpgsqlCommand command, DateTimeOffset value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.CreatedAt, value.ToUnixTimeSeconds(), index);

    public static NpgsqlCommand AddParamPayloadId(this NpgsqlCommand command, string value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.PayloadId, value, index);

    public static NpgsqlCommand AddParamTaskId(this NpgsqlCommand command, long value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.TaskId, value, index);


    public static NpgsqlCommand AddParamTaskCreatedAt(this NpgsqlCommand command, DateTimeOffset value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.TaskCreatedAt, value.ToUnixTimeSeconds(), index);


    sealed class BatchParams : INamePrefixProvider
    {
        public static int MaxIndex => 512;

        public static string[] GetPrefixes() =>
        [
            SqlParam.ErrorId
            , SqlParam.TypeName
            , SqlParam.StatusCode
            , SqlParam.StatusMessage
            , SqlParam.CreatedAt
            , SqlParam.PayloadId
            , SqlParam.TaskId
            , SqlParam.LockExpiresOn
            , SqlParam.TaskCreatedAt
        ];
    }
}