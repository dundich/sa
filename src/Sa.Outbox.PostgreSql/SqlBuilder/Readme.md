# SqlBuilder

## Overview

This folder holds the types responsible for generating and parameterising all SQL statements used by the PostgreSQL-backed outbox subsystem. Unlike the `Commands` folder (which translates high-level operations into concrete `NpgsqlCommand` calls), `SqlBuilder` owns the **SQL templates themselves** and the plumbing that makes them work at scale.

## File structure

| File | Responsibility |
|---|---|
| `SqlOutboxBuilder.cs` | Core class holding every SQL template string plus helper methods for dynamic value-list generation (error inserts, delivery inserts). Uses pooled `StringBuilder` for memory efficiency. |
| `SqlParam.cs` | Static class with named constants for every SQL parameter placeholder (e.g. `@tnt`, `@gr`, `@frm`). Ensures consistency across all queries. |
| `Setup.cs` | DI extension method (`AddOutboxSqlBuilder`) that registers `ObjectPool<StringBuilder>` and `SqlOutboxBuilder`. |

## Templates

All templates are built at construction time using column/table names resolved from `PgOutboxTableSettings`, so the resulting SQL is tailored to the consumer's schema layout.

### Static templates (read-only, constant strings)

| Field | Description |
|---|---|
| `SqlBulkMsgCopy` | Binary COPY-IN for bulk-inserting messages from STDIN |
| `SqlCreateTypeTable` | Creates the type lookup table if missing |
| `SqlSelectType` | Selects all rows from the type table |
| `SqlInsertType` | Inserts a new type row with `ON CONFLICT DO NOTHING` |
| `SqlCreateOffsetTable` | Creates the offset tracking table if missing |
| `SqlInsertOffset` / `SqlSelectOffset` / `SqlUpdateOffset` / `SqlInitOffset` | CRUD operations for consumer-group offset tracking |
| `SqlLockOffset` | Advisory lock (`pg_advisory_xact_lock`) for serialising offset writes |
| `SqlLockAndSelect` | CTE-based lock-and-fetch: selects pending/recoverable tasks, marks them `Processing`, joins with the message table, and returns payloads. Uses `FOR UPDATE SKIP LOCKED` for safe concurrent consumption |
| `SqlExtendDelivery` | Extends the lock TTL on a currently-processing task |
| `SqlLoadConsumerGroup` | Bulk-inserts tasks from the message table into the task queue, returning the last inserted ID |

### Dynamic templates (generated per-call)

| Method | Description |
|---|---|
| `SqlError(int count)` | Generates multi-row error insert statements parametrised by count |
| `SqlFinishDelivery(int count)` | Completes a delivery batch: inserts log rows, then updates the corresponding task records |

Both dynamic methods use `ObjectPool<StringBuilder>` to avoid heap allocations during high-throughput batching.

## Parameter naming convention

Every parameter follows a short mnemonic alias stored in `SqlParam`:

| Constant | Placeholder | Meaning |
|---|---|---|
| `TenantId` | `@tnt` | Tenant identifier |
| `ConsumerGroupId` | `@gr` | Consumer group identity |
| `MsgPart` | `@prt` | Message partition/part index |
| `TypeId` / `TypeName` | `@tp_id` / `@tp_nm` | Type registry key and name |
| `FromDate` / `ToDate` | `@frm` / `@to` | Time-range boundaries |
| `NowDate` | `@now` | Server-side `NOW()` substitute |
| `TransactId` | `@trn` | Transaction/session identifier |
| `Offset` | `@offset` | Cursor position for pagination |
| `Limit` | `@lim` | Max rows per batch |
| `LockOffset` | `@lck_id` | Advisory-lock numeric ID |
| `LockExpiresOn` | `@lck_on` | Lock expiry timestamp |
| `StatusCode` / `StatusMessage` | `@st_c` / `@st_m` | Delivery outcome code and text |
| `CreatedAt` | `@cr_at` | Row creation timestamp |
| `PayloadId` | `@p_id` | Message payload identifier |
| `TaskId` | `@tsk` | Task queue primary key |
| `TaskCreatedAt` | `@tsk_at` | Task record creation time |
| `ErrorId` | `@err_id` | Error record identifier |

## DI registration

`Setup.AddOutboxSqlBuilder` wires three things into the service collection:

1. `ObjectPoolProvider` → `DefaultObjectPoolProvider`
2. Pooled `ObjectPool<StringBuilder>` (initial capacity 1024) — reused during dynamic SQL generation to reduce GC pressure
3. Singleton `SqlOutboxBuilder`

It also delegates to `AddPgOutboxSettings` to register table/column configuration.

## Design notes

- **Pooled StringBuilder** — Dynamic multi-row value lists (errors, deliveries) use `ObjectPool<StringBuilder>` to avoid allocations during high-throughput batching.
- **Advisory locking** — Offset writes are serialised via `pg_advisory_xact_lock` to prevent race conditions without heavy table-level locks.
- **SKIP LOCKED** — Task selection uses PostgreSQL's `FOR UPDATE SKIP LOCKED` to allow multiple consumers to safely compete for work.
- **ON CONFLICT DO NOTHING** — Idempotent inserts protect against duplicate processing on retries.
- **Schema-aware templates** — Every template resolves table/column names from `PgOutboxTableSettings` at construction time, so no string concatenation happens at runtime.
