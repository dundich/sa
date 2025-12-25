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
  {settings.Message.Fields.MsgId},
  {settings.Message.Fields.TenantId},
  {settings.Message.Fields.MsgPart},
  {settings.Message.Fields.MsgPayloadId},
  {settings.Message.Fields.MsgPayloadType},
  {settings.Message.Fields.MsgPayload},
  {settings.Message.Fields.MsgPayloadSize},
  {settings.Message.Fields.MsgCreatedAt}
)
FROM STDIN (FORMAT BINARY)
;
""";



    public string SqlLockAndSelect =
$"""
WITH locked_tasks AS (
  SELECT 
    t.{settings.TaskQueue.Fields.TaskId},
    t.{settings.TaskQueue.Fields.TenantId},
    t.{settings.TaskQueue.Fields.ConsumerGroup},
    t.{settings.TaskQueue.Fields.MsgId}
  FROM {settings.GetQualifiedTaskTableName()} t
  WHERE
    t.{settings.TaskQueue.Fields.TenantId} = {SqlParam.TenantId}
    AND t.{settings.TaskQueue.Fields.ConsumerGroup} = {SqlParam.ConsumerGroupId}
    AND t.{settings.TaskQueue.Fields.TaskCreatedAt} >= {SqlParam.FromDate}
    AND t.{settings.TaskQueue.Fields.DeliveryStatusCode} IN (
      {(int)DeliveryStatusCode.Pending},
      {(int)DeliveryStatusCode.Processing},
      {(int)DeliveryStatusCode.Postpone},
      {(int)DeliveryStatusCode.Warn}
    )
    AND t.{settings.TaskQueue.Fields.TaskLockExpiresOn} < {SqlParam.ToDate}
    AND t.{settings.TaskQueue.Fields.MsgPart} = {SqlParam.MsgPart}
    AND t.{settings.TaskQueue.Fields.MsgPayloadType}={SqlParam.TypeId}
  ORDER BY t.{settings.TaskQueue.Fields.TaskId}
  LIMIT {SqlParam.Limit}
  FOR UPDATE SKIP LOCKED
),
updated_tasks AS (
  UPDATE {settings.GetQualifiedTaskTableName()} t
  SET
    {settings.TaskQueue.Fields.DeliveryStatusCode} = {(int)DeliveryStatusCode.Processing},
    {settings.TaskQueue.Fields.TaskTransactId} = {SqlParam.TransactId},
    {settings.TaskQueue.Fields.TaskLockExpiresOn} = {SqlParam.LockExpiresOn}
  FROM locked_tasks nt
  WHERE 
    t.{settings.TaskQueue.Fields.TaskId} = nt.{settings.TaskQueue.Fields.TaskId} 
    AND t.{settings.TaskQueue.Fields.TenantId} = nt.{settings.TaskQueue.Fields.TenantId}
    AND t.{settings.TaskQueue.Fields.ConsumerGroup} = nt.{settings.TaskQueue.Fields.ConsumerGroup}
  RETURNING 
    t.{settings.TaskQueue.Fields.TaskId},
    t.{settings.TaskQueue.Fields.TenantId},
    t.{settings.TaskQueue.Fields.ConsumerGroup},
    t.{settings.TaskQueue.Fields.MsgId},
    t.{settings.TaskQueue.Fields.MsgPart},
    t.{settings.TaskQueue.Fields.MsgPayloadId},
    t.{settings.TaskQueue.Fields.MsgPayloadType},
    t.{settings.TaskQueue.Fields.MsgCreatedAt},
    t.{settings.TaskQueue.Fields.DeliveryId},
    t.{settings.TaskQueue.Fields.DeliveryAttempt},
    t.{settings.TaskQueue.Fields.DeliveryStatusCode},
    t.{settings.TaskQueue.Fields.DeliveryStatusMessage},
    t.{settings.TaskQueue.Fields.DeliveryCreatedAt},
    t.{settings.TaskQueue.Fields.ErrorId},
    t.{settings.TaskQueue.Fields.TaskCreatedAt}
)
SELECT 
  ut.*,
  m.{settings.Message.Fields.MsgPayload}
FROM updated_tasks ut
INNER JOIN {settings.GetQualifiedMsgTableName()} m
  ON ut.{settings.TaskQueue.Fields.MsgId} = m.{settings.Message.Fields.MsgId}
WHERE 
  m.{settings.Message.Fields.TenantId} = {SqlParam.TenantId}
  AND m.{settings.Message.Fields.MsgPart} = {SqlParam.MsgPart}
  AND m.{settings.Message.Fields.MsgCreatedAt}>={SqlParam.FromDate}
  AND m.{settings.Message.Fields.MsgPayloadType}={SqlParam.TypeId}

ORDER BY ut.{settings.TaskQueue.Fields.TaskId}
""";


    public string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET {settings.TaskQueue.Fields.TaskLockExpiresOn}={SqlParam.LockExpiresOn}
WHERE 
  {settings.TaskQueue.Fields.TenantId}={SqlParam.TenantId}
  AND {settings.TaskQueue.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.TaskQueue.Fields.TaskCreatedAt}>={SqlParam.FromDate}
  AND {settings.TaskQueue.Fields.DeliveryStatusCode}={(int)DeliveryStatusCode.Processing}
  AND {settings.TaskQueue.Fields.TaskTransactId}={SqlParam.TransactId}
  AND {settings.Message.Fields.MsgPayloadType}={SqlParam.TypeId}
  AND {settings.TaskQueue.Fields.TaskLockExpiresOn}>{SqlParam.NowDate}
;
""";


    public string SqlCreateTypeTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTypeTableName()}
(
  {settings.Type.Fields.TypeId} BIGINT NOT NULL,
  {settings.Type.Fields.TypeName} TEXT NOT NULL,
  CONSTRAINT "pk_{settings.Type.TableName}" PRIMARY KEY ({settings.Type.Fields.TypeId})
)
;
""";


    public string SqlSelectType = $"SELECT * FROM {settings.GetQualifiedTypeTableName()}";


    public string SqlInsertType =
$"""
INSERT INTO {settings.GetQualifiedTypeTableName()} 
  ({settings.Type.Fields.TypeId},{settings.Type.Fields.TypeName})
VALUES
  ({SqlParam.TypeId},{SqlParam.TypeName})
ON CONFLICT DO NOTHING
;
""";


    public string SqlCreateOffsetTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedOffsetTableName()}
(
  {settings.Offset.Fields.ConsumerGroup} TEXT,
  {settings.Offset.Fields.TenantId} INT NOT NULL DEFAULT 0,
  {settings.Offset.Fields.GroupOffset} UUID NOT NULL DEFAULT '{Guid.Empty}',
  {settings.Offset.Fields.GroupUpdatedAt} TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  CONSTRAINT "pk_{settings.Offset.TableName}" PRIMARY KEY ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId})
)
;
""";


    public string SqlInsertOffset =
$"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
  ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId},{settings.Offset.Fields.GroupOffset})
VALUES 
  ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},{SqlParam.Offset})
ON CONFLICT ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId}) DO NOTHING;
;
""";


    public string SqlSelectOffset =
$"""
SELECT {settings.Offset.Fields.GroupOffset}
FROM {settings.GetQualifiedOffsetTableName()}
WHERE
  {settings.Offset.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.Offset.Fields.TenantId}={SqlParam.TenantId}
;
""";


    public string SqlUpdateOffset = $"""
UPDATE {settings.GetQualifiedOffsetTableName()}
SET 
  {settings.Offset.Fields.GroupOffset}={SqlParam.Offset}, 
  {settings.Offset.Fields.GroupUpdatedAt}=NOW()
WHERE 
  {settings.Offset.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.Offset.Fields.TenantId}={SqlParam.TenantId}
;
""";


    public string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()} 
  ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId})
VALUES 
  ({SqlParam.ConsumerGroupId},{SqlParam.TenantId})
ON CONFLICT ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId}) DO NOTHING
;
""";


    public string SqlLockOffset = $"SELECT pg_advisory_xact_lock({SqlParam.OffsetKey});";


    public string SqlLoadConsumerGroup = $"""
WITH inserted_rows AS(
  INSERT INTO {settings.GetQualifiedTaskTableName()}
    ({settings.TaskQueue.Fields.ConsumerGroup},
    {settings.TaskQueue.Fields.MsgId},
    {settings.TaskQueue.Fields.MsgPart},
    {settings.TaskQueue.Fields.TenantId},
    {settings.TaskQueue.Fields.MsgPayloadId},
    {settings.TaskQueue.Fields.MsgPayloadType},
    {settings.TaskQueue.Fields.MsgCreatedAt},
    {settings.TaskQueue.Fields.TaskCreatedAt})
  SELECT
    {SqlParam.ConsumerGroupId}
    ,{settings.Message.Fields.MsgId}
    ,{SqlParam.MsgPart}
    ,{SqlParam.TenantId}
    ,{settings.Message.Fields.MsgPayloadId}
    ,{settings.Message.Fields.MsgPayloadType}
    ,{settings.Message.Fields.MsgCreatedAt}
    ,{SqlParam.NowDate}
  FROM {settings.GetQualifiedMsgTableName()}
  WHERE
    {settings.Message.Fields.MsgPart}={SqlParam.MsgPart}
    AND {settings.Message.Fields.TenantId}={SqlParam.TenantId}
    AND {settings.Message.Fields.MsgCreatedAt}>={SqlParam.FromDate}
    AND {settings.Message.Fields.MsgCreatedAt}<={SqlParam.ToDate}
    AND {settings.Message.Fields.MsgId}>{SqlParam.Offset}
  ORDER BY {settings.Message.Fields.MsgId}
  LIMIT {SqlParam.Limit}
  RETURNING {settings.Message.Fields.MsgId}
)
SELECT
  COUNT(*) as copied_rows,
  (SELECT {settings.Message.Fields.MsgId} FROM inserted_rows ORDER BY {settings.Message.Fields.MsgId} DESC LIMIT 1) as max_id
FROM inserted_rows
;
""";


    public string SqlError(int count) => SqlError(settings, count);

    private static string SqlError(PgOutboxTableSettings settings, int count) =>
$"""
INSERT INTO {settings.GetQualifiedErrorTableName()} 
  ({settings.Error.Fields.ErrorId},{settings.Error.Fields.ErrorType},{settings.Error.Fields.ErrorMessage},{settings.Error.Fields.ErrorCreatedAt})
VALUES
{BuildErrorInsertValues(count)}
ON CONFLICT DO NOTHING
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


    public string SqlFinishDelivery(int count) => SqlFinishDelivery(settings, count);


    private static string SqlFinishDelivery(PgOutboxTableSettings settings, int count)
    {
        return
$"""
WITH inserted AS (
  INSERT INTO {settings.GetQualifiedDeliveryTableName()} (
    {settings.Delivery.Fields.DeliveryStatusCode}
    ,{settings.Delivery.Fields.DeliveryStatusMessage}
    ,{settings.Delivery.Fields.DeliveryCreatedAt}
    ,{settings.Delivery.Fields.MsgPayloadId}
    ,{settings.Delivery.Fields.TenantId}
    ,{settings.Delivery.Fields.ConsumerGroup}
    ,{settings.Delivery.Fields.TaskId}
    ,{settings.Delivery.Fields.TaskTransactId}
    ,{settings.Delivery.Fields.TaskLockExpiresOn}
    ,{settings.Delivery.Fields.TaskCreatedAt}
    ,{settings.Delivery.Fields.ErrorId}
  )
  VALUES
{BuildDeliveryInsertValues(count)}
  ON CONFLICT DO NOTHING
  RETURNING *
)
UPDATE {settings.GetQualifiedTaskTableName()} task
SET
  {settings.TaskQueue.Fields.DeliveryId}=inserted.{settings.Delivery.Fields.DeliveryId}
  , {settings.TaskQueue.Fields.DeliveryAttempt}=task.{settings.TaskQueue.Fields.DeliveryAttempt}
    + CASE WHEN {(int)DeliveryStatusCode.Postpone}<>inserted.{settings.Delivery.Fields.DeliveryStatusCode} 
        THEN 1
        ELSE 0
      END
  , {settings.TaskQueue.Fields.ErrorId}=inserted.{settings.Delivery.Fields.ErrorId}
  , {settings.TaskQueue.Fields.DeliveryStatusCode}=inserted.{settings.Delivery.Fields.DeliveryStatusCode}
  , {settings.TaskQueue.Fields.DeliveryStatusMessage}=inserted.{settings.Delivery.Fields.DeliveryStatusMessage}
  , {settings.TaskQueue.Fields.DeliveryCreatedAt}=inserted.{settings.Delivery.Fields.DeliveryCreatedAt}
  , {settings.TaskQueue.Fields.TaskLockExpiresOn}=inserted.{settings.Delivery.Fields.TaskLockExpiresOn}
FROM 
  inserted
WHERE 
  task.{settings.TaskQueue.Fields.TenantId}={SqlParam.TenantId}
  AND task.{settings.TaskQueue.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND task.{settings.TaskQueue.Fields.TaskId}=inserted.{settings.Delivery.Fields.TaskId}
  AND task.{settings.TaskQueue.Fields.TaskCreatedAt}>={SqlParam.FromDate}
  AND task.{settings.TaskQueue.Fields.TaskTransactId}={SqlParam.TransactId}
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
