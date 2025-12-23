using System.Text;

namespace Sa.Outbox.PostgreSql;

/// <summary>
/// Provides SQL query templates for working with PostgreSQL outbox tables.
/// </summary>
internal sealed class SqlOutboxTemplate(PgOutboxTableSettings settings)
{
    internal PgOutboxTableSettings Settings => settings;


    public string SqlBulkMsgCopy =
$"""
COPY {settings.GetQualifiedMsgTableName()} (
    {OutboxTableFields.Message.MsgId}
    ,{OutboxTableFields.Message.TenantId}
    ,{OutboxTableFields.Message.MsgPart}
    ,{OutboxTableFields.Message.MsgPayloadId}
    ,{OutboxTableFields.Message.MsgPayloadType}
    ,{OutboxTableFields.Message.MsgPayload}
    ,{OutboxTableFields.Message.MsgPayloadSize}
    ,{OutboxTableFields.Message.MsgCreatedAt}
)
FROM STDIN (FORMAT BINARY)
;
""";

    static readonly string s_InTaskProcessing =
        $"(delivery_status_code < {DeliveryStatusCode.Ok} OR delivery_status_code BETWEEN {DeliveryStatusCode.Status300} AND {DeliveryStatusCode.WarnEof})";

    public string SqlLockAndSelect =
$"""
WITH next_task AS (
  SELECT 
    task.{OutboxTableFields.TaskQueue.TaskId},
    task.{OutboxTableFields.TaskQueue.TenantId},
    task.{OutboxTableFields.TaskQueue.ConsumerGroup},
    msg.{OutboxTableFields.Message.MsgId},
    msg.{OutboxTableFields.Message.MsgPart},
    msg.{OutboxTableFields.Message.MsgPayload},
    msg.{OutboxTableFields.Message.MsgPayloadId},
    msg.{OutboxTableFields.Message.MsgPayloadType},
    msg.{OutboxTableFields.Message.MsgCreatedAt},
    task.{OutboxTableFields.TaskQueue.DeliveryId},
    task.{OutboxTableFields.TaskQueue.DeliveryAttempt},
    task.{OutboxTableFields.TaskQueue.DeliveryStatusCode},
    task.{OutboxTableFields.TaskQueue.DeliveryStatusMessage},
    task.{OutboxTableFields.TaskQueue.DeliveryCreatedAt},
    task.{OutboxTableFields.TaskQueue.ErrorId},
    task.{OutboxTableFields.TaskQueue.TaskCreatedAt}
  FROM {settings.GetQualifiedTaskTableName()} task
  INNER JOIN {settings.GetQualifiedMsgTableName()} msg 
    ON task.{OutboxTableFields.TaskQueue.MsgId} = msg.{OutboxTableFields.Message.MsgId}
  WHERE
    task.{OutboxTableFields.TaskQueue.TenantId} = {SqlParam.TenantId}
    AND task.{OutboxTableFields.TaskQueue.ConsumerGroup} = {SqlParam.ConsumerGroupId}
    AND task.{OutboxTableFields.TaskQueue.TaskCreatedAt} >= {SqlParam.FromDate}
    AND {s_InTaskProcessing}
    AND task.{OutboxTableFields.TaskQueue.TaskLockExpiresOn} < {SqlParam.ToDate}
    AND msg.{OutboxTableFields.Message.MsgPart} = {SqlParam.MsgPart}
    AND msg.{OutboxTableFields.Message.TenantId} = {SqlParam.TenantId}
    AND msg.{OutboxTableFields.Message.MsgCreatedAt} >= {SqlParam.FromDate}
    AND msg.{OutboxTableFields.Message.MsgPayloadType} = {SqlParam.TypeId}
  ORDER BY task.{OutboxTableFields.TaskQueue.TaskId}
  LIMIT {SqlParam.Limit}
  FOR UPDATE SKIP LOCKED
)
UPDATE {settings.GetQualifiedTaskTableName()} t
SET
  {OutboxTableFields.TaskQueue.DeliveryStatusCode} = {DeliveryStatusCode.Processing},
  {OutboxTableFields.TaskQueue.TaskTransactId} = {SqlParam.TransactId},
  {OutboxTableFields.TaskQueue.TaskLockExpiresOn} = {SqlParam.LockExpiresOn}
FROM next_task nt
WHERE 
  t.{OutboxTableFields.TaskQueue.TaskId} = nt.{OutboxTableFields.TaskQueue.TaskId} 
  AND t.{OutboxTableFields.TaskQueue.TenantId} = {SqlParam.TenantId}
  AND t.{OutboxTableFields.TaskQueue.ConsumerGroup} = {SqlParam.ConsumerGroupId} 
RETURNING nt.*
;
""";

    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET {OutboxTableFields.TaskQueue.TaskLockExpiresOn} = {SqlParam.LockExpiresOn}
WHERE  {OutboxTableFields.TaskQueue.TenantId} = {SqlParam.TenantId}
  AND {OutboxTableFields.TaskQueue.ConsumerGroup} = {SqlParam.ConsumerGroupId}
  AND {OutboxTableFields.TaskQueue.TaskCreatedAt} >= {SqlParam.FromDate}
  AND {s_InTaskProcessing}
  AND {OutboxTableFields.TaskQueue.TaskTransactId} = {SqlParam.TransactId}
  AND {OutboxTableFields.Message.MsgPayloadType} = {SqlParam.TypeId}
  AND {OutboxTableFields.TaskQueue.TaskLockExpiresOn} > {SqlParam.NowDate}
;
""";

    public string SqlCreateTypeTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTypeTableName()}
(
  {OutboxTableFields.TypeTable.TypeId} BIGINT NOT NULL,
  {OutboxTableFields.TypeTable.TypeName} TEXT NOT NULL,
  CONSTRAINT "pk_{settings.Type.TableName}" PRIMARY KEY ({OutboxTableFields.TypeTable.TypeId})
)
;
""";

    public string SqlSelectType = $"SELECT * FROM {settings.GetQualifiedTypeTableName()}";

    public string SqlInsertType =
$"""
INSERT INTO {settings.GetQualifiedTypeTableName()} 
  ({OutboxTableFields.TypeTable.TypeId}, {OutboxTableFields.TypeTable.TypeName})
VALUES
  ({SqlParam.TypeId},{SqlParam.TypeName})
ON CONFLICT DO NOTHING
;
""";

    public string SqlCreateOffsetTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedOffsetTableName()}
(
  {OutboxTableFields.Offset.ConsumerGroup} TEXT,
  {OutboxTableFields.Offset.TenantId} INT NOT NULL DEFAULT 0,
  {OutboxTableFields.Offset.GroupOffset} UUID NOT NULL DEFAULT '{Guid.Empty}',
  {OutboxTableFields.Offset.GroupUpdatedAt} TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  CONSTRAINT "pk_{settings.Offset.TableName}" PRIMARY KEY ({OutboxTableFields.Offset.ConsumerGroup}, {OutboxTableFields.Offset.TenantId})
)
;
""";

    public string SqlInsertOffset =
$"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
  ({OutboxTableFields.Offset.ConsumerGroup}, {OutboxTableFields.Offset.TenantId}, {OutboxTableFields.Offset.GroupOffset})
VALUES 
  ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},{SqlParam.Offset})
ON CONFLICT ({OutboxTableFields.Offset.ConsumerGroup}, {OutboxTableFields.Offset.TenantId}) DO NOTHING;
;
""";

    public string SqlSelectOffset =
$"""
SELECT {OutboxTableFields.Offset.GroupOffset} 
FROM {settings.GetQualifiedOffsetTableName()} 
WHERE 
  {OutboxTableFields.Offset.ConsumerGroup}={SqlParam.ConsumerGroupId} 
  AND {OutboxTableFields.Offset.TenantId}={SqlParam.TenantId}
;
""";

    public string SqlUpdateOffset = $"""
UPDATE {settings.GetQualifiedOffsetTableName()}
SET 
  {OutboxTableFields.Offset.GroupOffset}={SqlParam.Offset}, 
  {OutboxTableFields.Offset.GroupUpdatedAt}=NOW()
WHERE 
  {OutboxTableFields.Offset.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {OutboxTableFields.Offset.TenantId}={SqlParam.TenantId}
;
""";

    public string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
  ({OutboxTableFields.Offset.ConsumerGroup}, {OutboxTableFields.Offset.TenantId})
VALUES 
  ({SqlParam.ConsumerGroupId},{SqlParam.TenantId})
ON CONFLICT ({OutboxTableFields.Offset.ConsumerGroup}, {OutboxTableFields.Offset.TenantId}) DO NOTHING
;
""";

    public string SqlLockOffset = $"SELECT pg_advisory_xact_lock({SqlParam.OffsetKey});";

    public string SqlLoadConsumerGroup = $"""
WITH inserted_rows AS(
  INSERT INTO {settings.GetQualifiedTaskTableName()}
    ({OutboxTableFields.TaskQueue.ConsumerGroup},{OutboxTableFields.TaskQueue.TaskCreatedAt},{OutboxTableFields.TaskQueue.MsgId},{OutboxTableFields.TaskQueue.MsgPart},{OutboxTableFields.TaskQueue.TenantId},{OutboxTableFields.TaskQueue.MsgPayloadId},{OutboxTableFields.TaskQueue.MsgCreatedAt})
  SELECT
    {SqlParam.ConsumerGroupId}
    ,{SqlParam.NowDate}
    ,{OutboxTableFields.Message.MsgId}
    ,{SqlParam.MsgPart}
    ,{SqlParam.TenantId}
    ,{OutboxTableFields.Message.MsgPayloadId}
    ,{OutboxTableFields.Message.MsgCreatedAt}
  FROM {settings.GetQualifiedMsgTableName()}
  WHERE
    {OutboxTableFields.Message.MsgPart} = {SqlParam.MsgPart}
    AND {OutboxTableFields.Message.TenantId} = {SqlParam.TenantId}
    AND {OutboxTableFields.Message.MsgCreatedAt} >= {SqlParam.FromDate}
    AND {OutboxTableFields.Message.MsgCreatedAt} <= {SqlParam.ToDate}
    AND {OutboxTableFields.Message.MsgId} > {SqlParam.Offset}
  ORDER BY {OutboxTableFields.Message.MsgId}
  LIMIT {SqlParam.Limit}
  RETURNING {OutboxTableFields.Message.MsgId}
)
SELECT
  COUNT(*) as copied_rows,
  (SELECT {OutboxTableFields.Message.MsgId} FROM inserted_rows ORDER BY {OutboxTableFields.Message.MsgId} DESC LIMIT 1) as max_id
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
  ({OutboxTableFields.Error.ErrorId},{OutboxTableFields.Error.ErrorType},{OutboxTableFields.Error.ErrorMessage},{OutboxTableFields.Error.ErrorCreatedAt})
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
    {OutboxTableFields.Delivery.DeliveryStatusCode}
    , {OutboxTableFields.Delivery.DeliveryStatusMessage}
    , {OutboxTableFields.Delivery.DeliveryCreatedAt}
    , {OutboxTableFields.Delivery.MsgPayloadId}
    , {OutboxTableFields.Delivery.TenantId}
    , {OutboxTableFields.Delivery.ConsumerGroup}
    , {OutboxTableFields.Delivery.TaskId}
    , {OutboxTableFields.Delivery.TaskTransactId}
    , {OutboxTableFields.Delivery.TaskLockExpiresOn}
    , {OutboxTableFields.Delivery.TaskCreatedAt}
    , {OutboxTableFields.Delivery.ErrorId}
  )
  VALUES
{BuildDeliveryInsertValues(count)}
  ON CONFLICT DO NOTHING
  RETURNING *
)
UPDATE {settings.GetQualifiedTaskTableName()} task
SET
  {OutboxTableFields.TaskQueue.DeliveryId} = inserted_delivery.{OutboxTableFields.Delivery.DeliveryId}
  , {OutboxTableFields.TaskQueue.DeliveryAttempt} = task.{OutboxTableFields.TaskQueue.DeliveryAttempt}
    + CASE WHEN inserted_delivery.{OutboxTableFields.Delivery.DeliveryStatusCode} <> {DeliveryStatusCode.Postpone} 
        THEN 1
        ELSE 0
      END
  , {OutboxTableFields.TaskQueue.ErrorId} = inserted_delivery.{OutboxTableFields.Delivery.ErrorId}
  , {OutboxTableFields.TaskQueue.DeliveryStatusCode} = inserted_delivery.{OutboxTableFields.Delivery.DeliveryStatusCode}
  , {OutboxTableFields.TaskQueue.DeliveryStatusMessage} = inserted_delivery.{OutboxTableFields.Delivery.DeliveryStatusMessage}
  , {OutboxTableFields.TaskQueue.DeliveryCreatedAt} = inserted_delivery.{OutboxTableFields.Delivery.DeliveryCreatedAt}
  , {OutboxTableFields.TaskQueue.TaskLockExpiresOn} = inserted_delivery.{OutboxTableFields.Delivery.TaskLockExpiresOn}
FROM 
  inserted_delivery
WHERE 
  task.{OutboxTableFields.TaskQueue.TenantId} = {SqlParam.TenantId}
  AND task.{OutboxTableFields.TaskQueue.ConsumerGroup} = {SqlParam.ConsumerGroupId}
  AND task.{OutboxTableFields.TaskQueue.TaskId} = inserted_delivery.{OutboxTableFields.Delivery.TaskId}
  AND task.{OutboxTableFields.TaskQueue.TaskCreatedAt} >= {SqlParam.FromDate}
  AND task.{OutboxTableFields.TaskQueue.TaskTransactId} = {SqlParam.TransactId}
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
