using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
using System.Text;

namespace Sa.Outbox.PostgreSql.SqlBuilder;

/// <summary>
/// Provides SQL query templates for working with PostgreSQL outbox tables.
/// </summary>
/// <remarks>
/// Registered as a singleton (SqlBuilder/Setup.cs). Every template is an instance field built in
/// the constructor from the table settings — a scoped or transient registration would rebuild
/// ~20 string templates on every resolve. Changing the registration requires first making the
/// templates static and keyed by (schema, table) names. (M9)
/// </remarks>
internal sealed class SqlOutboxBuilder(
    PgOutboxTableSettings settings)
{
    internal PgOutboxTableSettings TableSettings => settings;

    private static readonly string LockAndSelectStatusCodes =
        $"{(int)DeliveryStatusCode.Pending}," +
        $"{(int)DeliveryStatusCode.Processing}," +
        $"{(int)DeliveryStatusCode.Postpone}," +
        $"{(int)DeliveryStatusCode.Retry}," +
        $"{(int)DeliveryStatusCode.Warn}";


    public readonly string SqlBulkMsgCopy =
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
    // NB: msg_seq is deliberately NOT in the COPY column list. It is BIGSERIAL — the database
    // assigns insertion order via the column DEFAULT (nextval) for every unlisted column, so
    // the sequence is what orders the cursor. The app must never supply msg_seq: a v7 id can
    // sort "in the past" relative to an already-advanced cursor (skewed clocks, backdating),
    // which is exactly the C2 message-loss the column exists to make impossible.



    // NB: the WHERE of `locked_tasks` must stay in sync with the WHERE of the final
    // SELECT ... JOIN. If a row is locked + flipped to Processing here but filtered out
    // there, the task is stranded in Processing with no delivery record and no payload
    // handed to the consumer. `msg_created_at` is denormalized in the task table, so the
    // message-side predicate can be mirrored here.
    //
    // NB: date parameter semantics. These three placeholders used to be `@frm` / `@to` / `@now`
    // and were shared with queries that meant something different by them. Current meanings:
    //
    //   @win_from  lower bound of the window this batch is drawn from (task and message side).
    //   @rty_at    a task is re-rentable only if its lock expired *before* this instant. Fed
    //              `now - BatchingWindow`, not `now` — that widening is deliberate, it is what
    //              makes an un-backoffed `Warn` wait out the batching window instead of retrying
    //              immediately (see Sa.Outbox/Delivery/Readme §4). Do not "fix" this to @now.
    //   @now       wall clock at call time.
    //
    // By contrast `@frm` in SqlExtendDelivery / SqlFinishDelivery is a *lookback* floor, and
    // `@now` in SqlLoadConsumerGroup is the value stamped as task_created_at. Same constants,
    // different meanings — which is exactly why the window parameters got their own names.
    public readonly string SqlLockAndSelect =
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
    AND t.{settings.TaskQueue.Fields.TaskCreatedAt} >= {SqlParam.WindowFrom}
    AND t.{settings.TaskQueue.Fields.MsgCreatedAt} >= {SqlParam.WindowFrom}
    AND t.{settings.TaskQueue.Fields.DeliveryStatusCode} IN (
      {LockAndSelectStatusCodes}
    )
    AND t.{settings.TaskQueue.Fields.TaskLockExpiresOn} < {SqlParam.RetryAfter}
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
  AND m.{settings.Message.Fields.MsgCreatedAt}>={SqlParam.WindowFrom}
  AND m.{settings.Message.Fields.MsgPayloadType}={SqlParam.TypeId}

ORDER BY ut.{settings.TaskQueue.Fields.TaskId}
""";


    public readonly string SqlExtendDelivery =
$"""
UPDATE {settings.GetQualifiedTaskTableName()}
SET {settings.TaskQueue.Fields.TaskLockExpiresOn}={SqlParam.LockExpiresOn}
WHERE
  {settings.TaskQueue.Fields.TenantId}={SqlParam.TenantId}
  AND {settings.TaskQueue.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.TaskQueue.Fields.TaskCreatedAt}>={SqlParam.FromDate}
  AND {settings.TaskQueue.Fields.DeliveryStatusCode}={(int)DeliveryStatusCode.Processing}
  AND {settings.TaskQueue.Fields.TaskTransactId}={SqlParam.TransactId}
  AND {settings.TaskQueue.Fields.MsgPayloadType}={SqlParam.TypeId}
  AND {settings.TaskQueue.Fields.TaskLockExpiresOn}>{SqlParam.NowDate}
;
""";


    public readonly string SqlCreateTypeTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedTypeTableName()}
(
  {settings.Type.Fields.TypeId} BIGINT NOT NULL,
  {settings.Type.Fields.TypeName} TEXT NOT NULL,
  CONSTRAINT "pk_{settings.Type.TableName}" PRIMARY KEY ({settings.Type.Fields.TypeId})
)
;
""";


    public readonly string SqlSelectType = $"SELECT * FROM {settings.GetQualifiedTypeTableName()}";


    // NB: `DISTINCT`, not a window function. The `ROW_NUMBER() OVER (PARTITION BY tenant_id)`
    // version had to materialize and sort one row per message before the outer filter threw
    // all but one away, so this query was the one place in the module where the *server's*
    // memory grew with the size of the outbox rather than with the number of tenants. The
    // message table is LIST-partitioned by (tenant_id, msg_part), so the aggregate collapses
    // over the partition key. No ORDER BY is promised by either form, and none is relied on:
    // DeliveryProcessor iterates the tenant set in whatever order it arrives.
    public readonly string SqlSelectTenant =
$"""
SELECT DISTINCT {settings.Message.Fields.TenantId}
FROM {settings.GetQualifiedMsgTableName()}
""";


    public readonly string SqlInsertType =
$"""
INSERT INTO {settings.GetQualifiedTypeTableName()}
  ({settings.Type.Fields.TypeId},{settings.Type.Fields.TypeName})
VALUES
  ({SqlParam.TypeId},{SqlParam.TypeName})
ON CONFLICT DO NOTHING
;
""";


    public readonly string SqlCreateOffsetTable =
$"""
CREATE TABLE IF NOT EXISTS {settings.GetQualifiedOffsetTableName()}
(
  {settings.Offset.Fields.ConsumerGroup} TEXT,
  {settings.Offset.Fields.TenantId} INT NOT NULL DEFAULT 0,
  {settings.Offset.Fields.GroupOffset} UUID NOT NULL DEFAULT '{Guid.Empty}', -- legacy cursor, kept for rolling upgrades; dropped by a later contract version
  {settings.Offset.Fields.GroupOffsetSeq} BIGINT NOT NULL DEFAULT 0, -- live cursor: msg_seq (C2)
  {settings.Offset.Fields.GroupUpdatedAt} TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  CONSTRAINT "pk_{settings.Offset.TableName}" PRIMARY KEY ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId})
)
;
""";


    public readonly string SqlSelectOffset =
$"""
SELECT {settings.Offset.Fields.GroupOffsetSeq}
FROM {settings.GetQualifiedOffsetTableName()}
WHERE
  {settings.Offset.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.Offset.Fields.TenantId}={SqlParam.TenantId}
;
""";


    public readonly string SqlUpdateOffset = $"""
UPDATE {settings.GetQualifiedOffsetTableName()}
SET
  {settings.Offset.Fields.GroupOffsetSeq}={SqlParam.Offset},
  {settings.Offset.Fields.GroupUpdatedAt}=NOW()
WHERE
  {settings.Offset.Fields.ConsumerGroup}={SqlParam.ConsumerGroupId}
  AND {settings.Offset.Fields.TenantId}={SqlParam.TenantId}
;
""";


    public readonly string SqlInitOffset = $"""
INSERT INTO {settings.GetQualifiedOffsetTableName()}
  ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId},{settings.Offset.Fields.GroupOffsetSeq})
VALUES
  ({SqlParam.ConsumerGroupId},{SqlParam.TenantId},{SqlParam.Offset})
ON CONFLICT ({settings.Offset.Fields.ConsumerGroup},{settings.Offset.Fields.TenantId}) DO NOTHING
;
""";


    public readonly string SqlLockOffset = $"SELECT pg_advisory_xact_lock(@lck_id);";


    // Legacy floor resolution (C2). With the cursor on msg_seq, a "start from this moment" floor
    // configured either as a v7 message id or as a date has to be translated into a seq boundary
    // once per consumer group (result cached by OutboxTaskLoader).
    //
    // Both queries return the *exclusive* lower boundary: the seq of the last message that still
    // sorts below the floor (0 when none). The loader compares `msg_seq > @offset` against it, so
    // the first message at or above the floor is the first delivered — exactly the strict
    // "first id/date strictly above the floor" semantics the old UUID cursor had, with no
    // off-by-one. A MIN(... >= floor) would need an offset of (seq-1) and collapse to 0 when
    // nothing qualifies yet, silently delivering everything below the floor; MAX(... < floor)
    // is well-defined in both cases precisely because msg_seq is contiguous over the msg table.
    //
    //   @msg_id   — cursor moves to just before "first message with id >= this". If every message
    //               with that id is gone (partition dropped), the next newer message is delivered.
    //   @flr_date — "start consuming from this moment" (the old ToMinGuidV7 semantics, now
    //               expressed in seq space); the boundary is simply the last message created
    //               strictly before the floor.
    public readonly string SqlResolveMsgIdFloor =
$"""
SELECT COALESCE(MAX({settings.Message.Fields.MsgSeq}), 0)
FROM {settings.GetQualifiedMsgTableName()}
WHERE
  {settings.Message.Fields.TenantId}={SqlParam.TenantId}
  AND {settings.Message.Fields.MsgId}<{SqlParam.MsgId}
;
""";

    public readonly string SqlResolveDateFloor =
$"""
SELECT COALESCE(MAX({settings.Message.Fields.MsgSeq}), 0)
FROM {settings.GetQualifiedMsgTableName()}
WHERE
  {settings.Message.Fields.TenantId}={SqlParam.TenantId}
  AND {settings.Message.Fields.MsgCreatedAt}<{SqlParam.FloorDate}
;
""";


    // C14-style idempotent schema upgrade, run from the add-post-sql hook on every migration pass.
    // A database created by an older version has no msg_seq at all; the columns are added in place
    // and stay no-ops on every later pass. Root tables only — the parent declaration propagates to
    // partitions created later automatically, and this library creates partitions itself, so no
    // per-partition ALTER is needed here.
    //
    // The msg/task upgrades make the *schema* of an existing deployment match the new binaries;
    // the offset upgrade adds the live BIGINT cursor next to the legacy UUID one (expand), so
    // the UUID column can keep serving old binaries during a rolling upgrade and is dropped by
    // a later contract version. This hook is the safe half — it must never rewrite cursor values.
    //
    // Converting a *populated* deployment still needs a manual data step, and the hooks above
    // cannot do it: they only add columns, whose defaults are 0. Two things have to be fixed by
    // an operator, once, before the new binaries serve that database.
    //
    //   1. Backfill msg_seq on the rows that predate the column. BIGSERIAL numbers the backfill
    //      in whatever order the UPDATE happens to touch rows, so if the historical msg_id order
    //      matters, renumber explicitly by msg_id instead of trusting the physical order.
    //   2. Seed group_offset_seq per consumer group. Skip this and every cursor starts at 0,
    //      because the predicate is `msg_seq > @offset`, so the entire history is delivered again
    //      — as new messages, to consumers that already processed it.
    //
    //   -- 1) renumber the old rows by msg_id (drop the ORDER BY if the physical order is fine)
    //   UPDATE <msg$table> m SET msg_seq = n.seq
    //   FROM (SELECT msg_id, ROW_NUMBER() OVER (ORDER BY msg_id) AS seq FROM <msg$table>) n
    //   WHERE m.msg_id = n.msg_id;
    //
    //   -- 2) seed each group's cursor at its highest existing task
    //   UPDATE <offset$table> o SET group_offset_seq = COALESCE((
    //     SELECT MAX(t.msg_seq) FROM <task$table> t
    //     WHERE t.consumer_group = o.consumer_group AND t.tenant_id = o.tenant_id), 0);
    //
    // The task table's msg_seq is denormalised and backfilled by the same statement that walks the
    // message table, so run 1 before 2. Both are idempotent in effect: re-running 2 lands on the
    // same MAX, and re-running 1 re-derives the same numbering from msg_id.
    public readonly string SqlAddMsgSeqColumnToMsgTable =
$"""
ALTER TABLE {settings.GetQualifiedMsgTableName()}
ADD COLUMN IF NOT EXISTS {settings.Message.Fields.MsgSeq} BIGSERIAL NOT NULL
;
""";

    public readonly string SqlAddMsgSeqColumnToTaskTable =
$"""
ALTER TABLE {settings.GetQualifiedTaskTableName()}
ADD COLUMN IF NOT EXISTS {settings.TaskQueue.Fields.MsgSeq} BIGINT NOT NULL DEFAULT 0
;
""";

    public readonly string SqlAddGroupOffsetSeqColumnToOffsetTable =
$"""
ALTER TABLE {settings.GetQualifiedOffsetTableName()}
ADD COLUMN IF NOT EXISTS {settings.Offset.Fields.GroupOffsetSeq} BIGINT NOT NULL DEFAULT 0
;
""";


    // NB: the cursor is `msg_seq`, the database-assigned insertion order, not `msg_id` (v7 UUID).
    // A v7 id is minted by application clocks, so a message inserted after the cursor advanced can
    // sort *below* it (skewed clocks between app instances, backdating) and would be skipped by
    // `msg_id > @offset` forever. `msg_seq` is assigned by the DB sequence at COPY time, so
    // "newer row" and "larger seq" are the same thing by construction. The ORDER BY seq + LIMIT
    // then walks a strict prefix of the message table, and the cursor (= max seq of the RETURNING
    // rows, which come from that same single data-modifying CTE) can never pass a not-yet-inserted
    // row. `task_created_at` stays @now — it is the task's own birthing time, unrelated to the cursor.
    public readonly string SqlLoadConsumerGroup = $"""
WITH inserted_rows AS(
  INSERT INTO {settings.GetQualifiedTaskTableName()}
    ({settings.TaskQueue.Fields.ConsumerGroup},
    {settings.TaskQueue.Fields.MsgId},
    {settings.TaskQueue.Fields.MsgSeq},
    {settings.TaskQueue.Fields.MsgPart},
    {settings.TaskQueue.Fields.TenantId},
    {settings.TaskQueue.Fields.MsgPayloadId},
    {settings.TaskQueue.Fields.MsgPayloadType},
    {settings.TaskQueue.Fields.MsgCreatedAt},
    {settings.TaskQueue.Fields.TaskCreatedAt})
  SELECT
    {SqlParam.ConsumerGroupId}
    ,{settings.Message.Fields.MsgId}
    ,{settings.Message.Fields.MsgSeq}
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
    AND {settings.Message.Fields.MsgCreatedAt}>={SqlParam.WindowFrom}
    AND {settings.Message.Fields.MsgCreatedAt}<={SqlParam.WindowTo}
    AND {settings.Message.Fields.MsgSeq}>{SqlParam.Offset}
    AND {settings.Message.Fields.MsgPayloadType}={SqlParam.TypeId}
  ORDER BY {settings.Message.Fields.MsgSeq}
  LIMIT {SqlParam.Limit}
  RETURNING {settings.Message.Fields.MsgSeq}
)
SELECT
  COUNT(*) AS copied_rows,
  (SELECT {settings.Message.Fields.MsgSeq} FROM inserted_rows
    ORDER BY {settings.Message.Fields.MsgSeq} DESC LIMIT 1) AS max_seq
FROM inserted_rows
;
""";


    public string SqlError(int count) => SqlError(settings, count);

    // NB: this `ON CONFLICT DO NOTHING` is load-bearing, unlike the one in SqlFinishDelivery.
    // The log table's primary key is (delivery_id, ...) with `delivery_id` BIGSERIAL, so a
    // conflict cannot occur and the clause there is dead weight. Here the primary key is
    // (error_id, error_created_at) where `error_id` is a hash of the error text and
    // `error_created_at` is truncated to the start of the day — so the *same* failure
    // reported in *two* batches on the same day collides, and this clause is what keeps
    // `__error$` from growing one row per batch. ErrorDeliveryCommand.GroupByException
    // relies on that dedup. Do not "clean this up" by symmetry with SqlFinishDelivery.
    private string SqlError(PgOutboxTableSettings settings, int count) =>
$"""
INSERT INTO {settings.GetQualifiedErrorTableName()}
  ({settings.Error.Fields.ErrorId},{settings.Error.Fields.ErrorType},{settings.Error.Fields.ErrorMessage},{settings.Error.Fields.ErrorCreatedAt})
VALUES
{BuildErrorInsertValues(count)}
ON CONFLICT DO NOTHING
;
""";

    private string BuildErrorInsertValues(int count)
    {
        // ~40–48 bytes per VALUES row (four indexed param names + separators). The estimate is a
        // floor — StringBuilder grows amortized if names run longer. No pool: these builders run
        // at most a handful of times per process (the SQL cache memoizes by chunk length), so an
        // ObjectPool<StringBuilder> here was pure overhead (M1).
        var sb = new StringBuilder(count * 48);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(",\n");

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


    // NB: the `delivery_attempt` CASE below deliberately exempts `Postpone` (103) from the attempt
    // counter. Deferral is fully consumer-governed: the consumer decides when — and whether — to give
    // up, so `MaxDeliveryAttempts` applies to failures (Warn), not to postponements. A consumer that
    // postpones indefinitely owns the dead-lettering decision itself: it must eventually call
    // Error/Error5xx (terminal) or start failing, at which point MaxDeliveryAttempts takes over.
    // This is a design decision, not an oversight — do not "fix" it by incrementing for 103 as well.
    public string SqlFinishDelivery(int count) => SqlFinishDelivery(settings, count);


    private string SqlFinishDelivery(PgOutboxTableSettings settings, int count)
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
  -- No ON CONFLICT clause here on purpose. The primary key of the log table is
  -- (delivery_id, ...) and `delivery_id` is BIGSERIAL, so a conflict is not merely unlikely,
  -- it is impossible. A real idempotency guarantee (same logical delivery retried) would need
  -- a separate UNIQUE key derived from the delivery identity; that is stage-3 work, not this.
  -- Do not copy the `ON CONFLICT DO NOTHING` from SqlError / SqlInsertType onto this insert:
  -- over there the conflict is genuine, over here the clause could only ever hide a bug.
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

    private string BuildDeliveryInsertValues(int count)
    {
        // ~90–96 bytes per row: 8 indexed param names + the 3 common names (@tnt/@gr/@trn) repeated
        // in every VALUES row. Floor, not exact — see BuildErrorInsertValues. No pool (M1).
        var sb = new StringBuilder(count * 96);
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

