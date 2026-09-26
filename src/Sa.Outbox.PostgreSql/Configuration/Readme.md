# Configuration

## Overview

---

## Registration flow (DI)

### Entry chain

```
services.AddSaOutboxUsingPostgreSql(configure)   // package root: Setup.cs (public)
  → AddOutboxSqlBuilder(configure)               // SqlBuilder/Setup.cs (internal)
      → AddPgOutboxSettings(configure)           // Configuration/Setup.cs (internal)
```

`AddPgOutboxSettings` constructs the configuration object and applies the defaults **before** the user delegate:

```csharp
var configuration = new PgOutboxConfiguration(services)
    .WithDefaultSerializer()   // ①
    .WithOutboxSettings()      // ②
    .WithDataSource();         // ③

configure?.Invoke(configuration);   // ④ user delegate — always runs last
```

### ① Default serializer

`WithDefaultSerializer()` is an `internal` method (not part of `IPgOutboxConfiguration`). It registers the shared `OutboxMessageSerializer.Instance` as a singleton of `IOutboxMessageSerializer` using `TryAddSingleton` — so any `IOutboxMessageSerializer` registered earlier in your code wins, and a later `WithMessageSerializer(...)` call removes and replaces it.

### ② Outbox settings

`WithOutboxSettings(configure)`:

1. If a delegate is passed, it is registered as a singleton of `Action<IServiceProvider, PgOutboxSettings>`.
2. `RegisterOutboxSettings()` registers the singleton `PgOutboxSettings` via a **factory** (the object is built lazily on first resolution, once per container):
   - `new PgOutboxSettings()` — all four sub-settings start as `new()`.
   - **Schema auto-inheritance**: the factory resolves `IPgDataSource` and reads `GetSearchPath()` (the Npgsql `Search Path` key of the connection string, or `"public"` if absent/unparseable). If the value is non-empty and non-whitespace, it is applied as `settings.TableSettings.WithSchema(...)`. This runs **before** the user delegates, so a user `WithSchema("x")` always wins over the connection's search path.
   - **All** registered `Action<IServiceProvider, PgOutboxSettings>` delegates are then invoked on this single instance, in registration order. The last write wins per property.
3. `RegisterComponentSettings()` registers four more singletons as **projections** of `PgOutboxSettings`:

   | Registered type | Projects |
   |---|---|
   | `PgOutboxTableSettings` | `PgOutboxSettings.TableSettings` |
   | `PgOutboxMigrationSettings` | `PgOutboxSettings.MigrationSettings` |
   | `PgOutboxCleanupSettings` | `PgOutboxSettings.CleanupSettings` |
   | `PgOutboxConsumeSettings` | `PgOutboxSettings.ConsumeSettings` |

   All use `TryAddSingleton`, so any injected `PgOutboxMigrationSettings` is the exact same object instance as `PgOutboxSettings.MigrationSettings`.

### ③ Data source

`WithDataSource(configure)` delegates to `AddSaPostgreSqlDataSource(configure)` from `Sa.Data.PostgreSql`:

- The `IPgDataSourceSettingsBuilder` receives `WithConnectionString(string)` and `WithConnectionString(Func<IServiceProvider, string>)`; either registers `PgDataSourceSettings` as a singleton (`TryAddSingleton`).
- `IPgDataSource` is registered as a singleton; its factory reads `PgDataSourceSettings` if present, otherwise falls back to the connection string of an `NpgsqlDataSource` registered in DI, and throws `InvalidOperationException("Empty connection string")` if neither is available.
- `PgDataSourceSettings.GetSearchPath()` is the source for the schema auto-inheritance in step ②.

### ④ User delegate

Your `configure` delegate runs **last** (after all defaults). Its three public methods:

- `WithMessageSerializer(...)` — removes every existing `IOutboxMessageSerializer` registration (`RemoveAll`) and adds exactly one new one (`TryAddSingleton`), so it always wins over the default JSON serializer.
- `WithOutboxSettings(userAction)` — appends your delegate to the registered list of `Action<IServiceProvider, PgOutboxSettings>`; it will run after any previously registered delegate.
- `WithDataSource(action)` — registers `PgDataSourceSettings` via `TryAddSingleton`, so only the first registration wins; the `IPgDataSource` factory (already registered in ③) picks it up lazily at resolution time.

### Where the values are actually consumed

| Settings | Consumer | Effect |
|---|---|---|
| `PgOutboxTableSettings` | `SqlOutboxBuilder` (all SQL templates) and `Partitional/Setup.cs` | Schema / table / column names are interpolated into every SQL statement; table creation uses `PartByList` (tenant + part / consumer group), `TimestampAs`, and `WithFillFactor` from these settings |
| `PgOutboxMigrationSettings` | `AddPartMigrationSchedule` → `Sa.Partitional.PostgreSql` migration job | The job `StartImmediate()` + `EveryTime(ExecutionInterval)`; each run ensures daily partitions exist for today .. today + `ForwardDays − 1`; `AsBackgroundJob = false` → job `Disabled()` (still resolvable via `IMigrationService` for manual runs) |
| `PgOutboxCleanupSettings` | `AddPartCleanupSchedule` → `Sa.Partitional.PostgreSql` cleanup job | The job runs `EveryTime(ExecutionInterval)`; each run drops partitions older than `now − DropPartsAfterRetention` on every outbox table; `AsBackgroundJob = false` → job `Disabled()` (still resolvable via `IPartCleanupService`) |
| `PgOutboxConsumeSettings` | `OutboxTaskLoader` | Floors the persisted offset per consumer group — see the `PgOutboxConsumeSettings` section |

---

## IPgOutboxConfiguration

The public fluent surface received as the `configure` argument of `AddSaOutboxUsingPostgreSql(...)`.

| Method | Purpose |
|---|---|
| `WithMessageSerializer(Func<IServiceProvider, IOutboxMessageSerializer>)` | Replace the serializer with a factory resolved from DI |
| `WithMessageSerializer<TService>(TService instance)` | Replace the serializer with a pre-created instance |
| `WithMessageSerializer<TService>()` | Register a serializer type with a parameterless constructor |
| `WithOutboxSettings(Action<IServiceProvider, PgOutboxSettings>?)` | Register a delegate that customizes `PgOutboxSettings` (table / migration / cleanup / consume) |
| `WithDataSource(Action<IPgDataSourceSettingsBuilder>?)` | Configure the PostgreSQL connection string |

All methods return the same `IPgOutboxConfiguration` for chaining.

### `WithMessageSerializer` — three overloads

Each overload first removes all existing `IOutboxMessageSerializer` registrations (`RemoveAll`) and then adds exactly one, so any overload always replaces the default JSON serializer.

| Overload | DI registration | When to use |
|---|---|---|
| `Func<IServiceProvider, IOutboxMessageSerializer>` | `TryAddSingleton` with a factory | The serializer depends on DI — e.g. reads options from `IConfiguration`, or obtains a source-generated `JsonSerializerContext` from the container |
| `TService instance` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton` with the instance | You already have a pre-built serializer (custom options, injected dependencies in the constructor); it is used directly, never resolved from DI |
| `TService` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton<IOutboxMessageSerializer, TService>` | Simple stateless serializer with a parameterless constructor. The XML doc calls this "transient", but the code registers a **singleton** — the same instance is reused for the container's lifetime |

### `WithOutboxSettings`

Takes an optional `Action<IServiceProvider, PgOutboxSettings>`. When the delegate is non-null it is stored as a singleton and later applied inside the `PgOutboxSettings` factory (see Registration flow ②). The `IServiceProvider` parameter is available if you need to read other registered services while configuring (e.g. `IConfiguration`).

### `WithDataSource`

Takes an optional `Action<IPgDataSourceSettingsBuilder>`. The only options exposed through the builder are the connection string (plain string or a factory from DI). Pooling and other Npgsql options are configured inside the connection string itself (e.g. `Minimum Pool Size`, `Maximum Pool Size`, `Search Path`, `Timeout`); `Search Path` additionally drives the automatic schema inheritance of the outbox tables.

---

## PgOutboxSettings

The root settings object (public, sealed). All four properties are **get-only** and initialized with `new()`; you never replace a sub-settings object — you mutate it through its properties or fluent methods.

```
PgOutboxSettings
├── TableSettings    (PgOutboxTableSettings)    — schema, six tables, column names, fill factors
├── MigrationSettings (PgOutboxMigrationSettings) — partition-creation schedule
├── CleanupSettings   (PgOutboxCleanupSettings)  — partition-dropping schedule
└── ConsumeSettings   (PgOutboxConsumeSettings)  — per-consumer-group minimum offsets
```

| Property | Type | Default | Description |
|---|---|---|---|
| `TableSettings` | `PgOutboxTableSettings` | `new()` | Schema and all six outbox tables (names, columns, fill factors) |
| `MigrationSettings` | `PgOutboxMigrationSettings` | `new()` | How often and how far ahead partition migration runs |
| `CleanupSettings` | `PgOutboxCleanupSettings` | `new()` | How often old partitions are dropped and the retention window |
| `ConsumeSettings` | `PgOutboxConsumeSettings` | `new()` | Minimum offsets per consumer group |

> Note: the class XML comment also mentions "serialization, caching" — there are no such sub-settings in this class; that wording is stale.

---

## PgOutboxTableSettings

### Top-level properties

| Property | Type | Default | Description |
|---|---|---|---|
| `DatabaseSchemaName` | `string` | `"public"` | Schema that owns all six outbox tables. Auto-inherited from the connection's `Search Path` before user configuration; set it explicitly to override |
| `TaskQueue` | `TaskQueueTable` | `new()` | The active task queue (read-write, `SKIP LOCKED`) — table name = base name |
| `Message` | `MessageTable` | `new()` | Source messages (read-only, bulk BINARY COPY target) |
| `Delivery` | `DeliveryTable` | `new()` | Delivery history (read-only) |
| `Error` | `ErrorTable` | `new()` | Permanent delivery errors |
| `Type` | `TypeTable` | `new()` | Message-type registry (type id ↔ name); **no `FillFactor` property** |
| `Offset` | `OffsetTable` | `new()` | Consumer-group offsets (advisory-locked); **no `FillFactor` property** |

A public static nested class `Defaults` holds the two name constants: `DatabaseSchemaName = "public"` and `DatabaseTableName = "outbox"` (the default base table name from which all six names derive).

### Tables

| Settings property | Class | Default table name | Suffix | `FillFactor` default | Role |
|---|---|---|---|---|---|
| `TaskQueue` | `TaskQueueTable` | `outbox` | — | `65` | Active task queue, read-write (`SKIP LOCKED`); 35% free space keeps updates in place |
| `Message` | `MessageTable` | `outbox__msg$` | `__msg$` | `100` | Source messages, append-only (BINARY COPY), never updated |
| `Delivery` | `DeliveryTable` | `outbox__log$` | `__log$` | `100` | Delivery history, read-only |
| `Error` | `ErrorTable` | `outbox__error$` | `__error$` | `100` | Permanent errors, read-only |
| `Type` | `TypeTable` | `outbox__type$` | `__type$` | — (no property) | Type registry; created by the auxiliary `SqlCreateTypeTable` statement (plain `CREATE TABLE IF NOT EXISTS`, PostgreSQL default fill factor) |
| `Offset` | `OffsetTable` | `outbox__offset$` | `__offset$` | — (no property) | Offsets per consumer group + tenant; created by the auxiliary `SqlCreateOffsetTable` statement |

Every `FillFactor` setter validates the value: `ArgumentOutOfRangeException` outside `1..100` (PostgreSQL constraint).

### Columns

Each table exposes a get-only `Fields` object holding one string property per column; every property defaults to the matching `OutboxFieldDefaults` constant. The `Fields.All()` method returns the exact PostgreSQL column definitions used at table creation.

#### Message table (`__msg$`) — 8 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `MsgId` | `msg_id` | `UUID NOT NULL` | Message id — UUID v7, generated by the application; the partition sort key and the consumption offset |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant id; first partitioning key |
| `MsgPart` | `msg_part` | `TEXT NOT NULL` | Message part; second partitioning key |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Optional payload id supplied by the publisher |
| `MsgPayloadType` | `msg_payload_type` | `BIGINT NOT NULL DEFAULT 0` | Hash of the payload type (murmurHash3), matching the `__type$` registry |
| `MsgPayload` | `msg_payload` | `BYTEA NOT NULL` | Serialized message payload |
| `MsgPayloadSize` | `msg_payload_size` | `INT NOT NULL DEFAULT 0` | Payload size in bytes |
| `MsgCreatedAt` | `msg_created_at` | `BIGINT NOT NULL DEFAULT 0` | Creation time (ticks); partition timestamp column |

#### Task queue table (`outbox`) — 17 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `TaskId` | `task_id` | `BIGSERIAL NOT NULL` | Task surrogate id |
| `ConsumerGroup` | `consumer_group` | `TEXT NOT NULL` | Consumer group; second partitioning key |
| `TaskLockExpiresOn` | `task_lock_expires_on` | `BIGINT NOT NULL DEFAULT 0` | Lock TTL (ticks) — consumers competing with `SKIP LOCKED` skip rows whose lock is still valid |
| `TaskTransactId` | `task_transact_id` | `TEXT NOT NULL DEFAULT ''` | Transaction / correlation id of the worker processing the task |
| `MsgId` | `msg_id` | `UUID NOT NULL` | Reference to the source message (v7 UUID) |
| `MsgPart` | `msg_part` | `TEXT NOT NULL` | Message part (denormalized) |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant id; first partitioning key |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Payload id (denormalized) |
| `MsgPayloadType` | `msg_payload_type` | `BIGINT NOT NULL DEFAULT 0` | Type hash (denormalized) |
| `MsgCreatedAt` | `msg_created_at` | `BIGINT NOT NULL DEFAULT 0` | Message creation time (denormalized) |
| `DeliveryId` | `delivery_id` | `BIGINT NOT NULL DEFAULT 0` | Id of the delivery record in `__log$` |
| `DeliveryAttempt` | `delivery_attempt` | `INT NOT NULL DEFAULT 0` | Delivery attempt counter |
| `DeliveryStatusCode` | `delivery_status_code` | `INT NOT NULL DEFAULT 0` | Delivery status code; `0` = `DeliveryStatusCode.Pending` |
| `DeliveryStatusMessage` | `delivery_status_message` | `TEXT NOT NULL DEFAULT ''` | Free-text status / error message |
| `DeliveryCreatedAt` | `delivery_created_at` | `BIGINT NOT NULL DEFAULT 0` | Time the delivery started (ticks) |
| `ErrorId` | `error_id` | `BIGINT NOT NULL DEFAULT 0` | Reference to the `__error$` row for permanent failures |
| `TaskCreatedAt` | `task_created_at` | `BIGINT NOT NULL DEFAULT 0` | Task creation time; partition timestamp column |

#### Delivery table (`__log$`) — 12 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `DeliveryId` | `delivery_id` | `BIGSERIAL NOT NULL` | Delivery record id |
| `DeliveryStatusCode` | `delivery_status_code` | `INT NOT NULL DEFAULT 0` | Final delivery status; `0` = `Pending` |
| `DeliveryStatusMessage` | `delivery_status_message` | `TEXT NOT NULL DEFAULT ''` | Status / error message |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Payload id |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant; first partitioning key |
| `ConsumerGroup` | `consumer_group` | `TEXT NOT NULL` | Consumer group; second partitioning key |
| `TaskId` | `task_id` | `BIGINT NOT NULL DEFAULT 0` | Source task id |
| `TaskCreatedAt` | `task_created_at` | `BIGINT NOT NULL DEFAULT 0` | Task creation time |
| `TaskTransactId` | `task_transact_id` | `TEXT NOT NULL DEFAULT ''` | Worker transaction id |
| `TaskLockExpiresOn` | `task_lock_expires_on` | `BIGINT NOT NULL DEFAULT 0` | Lock TTL at delivery time |
| `ErrorId` | `error_id` | `BIGINT NOT NULL DEFAULT 0` | Reference to `__error$` if the delivery ended in a permanent error |
| `DeliveryCreatedAt` | `delivery_created_at` | `BIGINT NOT NULL DEFAULT 0` | Delivery creation time; partition timestamp column |

#### Error table (`__error$`) — 4 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `ErrorId` | `error_id` | `BIGINT NOT NULL` | Error record id |
| `ErrorType` | `error_type` | `TEXT NOT NULL` | Error type name |
| `ErrorMessage` | `error_message` | `TEXT NOT NULL DEFAULT ''` | Error message |
| `ErrorCreatedAt` | `error_created_at` | `BIGINT NOT NULL DEFAULT 0` | Creation time; partition timestamp column |

#### Type table (`__type$`) — 2 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `TypeId` | `type_id` | `BIGINT NOT NULL` | Type hash (murmurHash3) — primary key |
| `TypeName` | `type_name` | `TEXT NOT NULL` | Full type name |

#### Offset table (`__offset$`) — 4 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `ConsumerGroup` | `consumer_group` | `TEXT` (nullable) | Consumer group id — part of the primary key |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant — part of the primary key |
| `GroupOffset` | `group_offset` | `UUID NOT NULL DEFAULT '{Guid.Empty}'` | Last consumed message id (UUID v7) — the offset is a v7 UUID, so it is time-ordered |
| `GroupUpdatedAt` | `group_updated_at` | `TIMESTAMP WITH TIME ZONE DEFAULT NOW()` | Last offset update |

### Fluent methods (`PgOutboxTableSettingsExtensions`)

| Method | Effect |
|---|---|
| `GetQualifiedMsgTableName()` / `GetQualifiedDeliveryTableName()` / `GetQualifiedTypeTableName()` / `GetQualifiedOffsetTableName()` / `GetQualifiedErrorTableName()` / `GetQualifiedTaskTableName()` | Return `"{DatabaseSchemaName}.\"{table}\""` for the corresponding table — exactly the form used in every generated SQL statement |
| `UseBaseTableName(string baseTableName)` | Renames **all six** tables from a single base name: task queue = `base`, message = `base__msg$`, delivery = `base__log$`, type = `base__type$`, offset = `base__offset$`, error = `base__error$`. Throws `ArgumentException` on null/whitespace |
| `UseBaseTableName(string schemaName, string baseTableName)` | Same as above plus sets `DatabaseSchemaName = schemaName`. Throws `ArgumentException` on null/whitespace schema |
| `WithSchema(string schemaName)` | Sets `DatabaseSchemaName` only. Throws `ArgumentNullException` on null/whitespace |
| `WithMsgTableName(string tableName)` | Renames only the message table |
| `WithDeliveryTableName(string tableName)` | Renames only the delivery table |
| `WithTypeTableName(string tableName)` | Renames only the type table |
| `WithOffsetTableName(string tableName)` | Renames only the offset table |
| `WithErrorTableName(string tableName)` | Renames only the error table |

All `With*TableName` overloads throw `ArgumentNullException` on null/whitespace. Note there is **no** fluent method to rename only the task queue table — use `UseBaseTableName(...)` (which sets the task queue name to the base name) or set the `TaskQueue.TableName` property directly.

### Example

```csharp
settings.TableSettings
    .UseBaseTableName("outbox")        // outbox, outbox__msg$, outbox__log$, outbox__type$, outbox__offset$, outbox__error$
    .WithSchema("my_outbox");

// per-table fill factors
settings.TableSettings.TaskQueue.FillFactor = 65;   // read-write — keep free space for updates
settings.TableSettings.Message.FillFactor = 100;    // append-only

// rename individual columns
settings.TableSettings.Message.Fields =
{
    MsgId = "id",
    MsgPayload = "payload_bytes"
};
```

---

## PgOutboxMigrationSettings

Controls the partition-creation job (daily partitions are created for the time-sorted columns).

| Property | Type | Default | Description |
|---|---|---|---|
| `AsBackgroundJob` | `bool` | `true` | `true` — the migration job is scheduled: it starts immediately on host startup and then repeats every `ExecutionInterval`. `false` — the job is registered but `Disabled()`; the `IMigrationService` is still resolvable so you can run a migration manually (e.g. `WaitMigration(...)`) |
| `ForwardDays` | `int` | `2` | How far ahead the migration moves: each run ensures partitions exist for today .. today + `ForwardDays − 1` (i.e. `ForwardDays = 2` → today and tomorrow). This is what keeps the partitioning "ahead of the clock" without manual DDL |
| `ExecutionInterval` | `TimeSpan` | `6 h` + random 1–58 min | Repeat interval of the migration job. The default is not a fixed 6 hours: `6 h + Random.Shared.Next(1, 59) minutes` (roll happens when the settings object is constructed). The jitter de-synchronizes several replicas/instances so they don't all try to migrate in the same instant |

### Fluent methods (`PgOutboxMigrationSettingsExtensions`)

| Method | Effect |
|---|---|
| `RunAsJob()` | `AsBackgroundJob = true` |
| `WithExecutionInterval(TimeSpan interval)` | Sets `ExecutionInterval`; throws `ArgumentException` if `interval <= TimeSpan.Zero` |
| `UseProductionSettings()` | `AsBackgroundJob = true`, `ForwardDays = 7`, `ExecutionInterval = 6 h + 1–58 min jitter` |
| `UseDevelopmentSettings()` | `AsBackgroundJob = true`, `ForwardDays = 1`, `ExecutionInterval = 1 h` |
| `UseTestSettings()` | `AsBackgroundJob = false`, `ForwardDays = 0` (no forward movement), `ExecutionInterval = 0` |

### Example

```csharp
settings.MigrationSettings.UseProductionSettings();
// or precisely:
settings.MigrationSettings
    .RunAsJob()
    .WithExecutionInterval(TimeSpan.FromHours(6));
settings.MigrationSettings.ForwardDays = 3;
```

---

## PgOutboxCleanupSettings

Controls the partition-dropping job: daily partitions older than the retention window are dropped from every outbox table.

| Property | Type | Default | Description |
|---|---|---|---|
| `AsBackgroundJob` | `bool` | `true` | `true` — the cleanup job runs on schedule. `false` — the job is `Disabled()`; `IPartCleanupService` stays resolvable for manual runs |
| `DropPartsAfterRetention` | `TimeSpan` | `30 days` | Retention window: each run drops partitions whose partition timestamp is older than `now − DropPartsAfterRetention`. E.g. with the default, yesterday's partitions are dropped once they are more than 30 days old |
| `ExecutionInterval` | `TimeSpan` | `4 h` | Repeat interval of the cleanup job (the property default is a fixed 4 hours, no jitter — jitter is added only by `UseProductionSettings()`) |

### Fluent methods (`PgOutboxCleanupSettingsExtensions`)

| Method | Effect |
|---|---|
| `RunAsJob()` | `AsBackgroundJob = true` |
| `RunImmediately()` | `AsBackgroundJob = false` |
| `SetAsJob(bool asJob)` | Sets `AsBackgroundJob` explicitly |
| `WithRetentionPeriod(TimeSpan retentionPeriod)` | Sets `DropPartsAfterRetention`; throws `ArgumentException` if `<= TimeSpan.Zero` |
| `WithRetentionDays(int days)` | `DropPartsAfterRetention = TimeSpan.FromDays(days)`; throws `ArgumentException` if `days <= 0` |
| `KeepForever()` | `DropPartsAfterRetention = TimeSpan.MaxValue` — never drop parts |
| `UseAggressiveCleanup()` | `AsBackgroundJob = true`, retention `7 days`, interval `1 h` |
| `UseConservativeCleanup()` | `AsBackgroundJob = true`, retention `90 days`, interval `1 day` |
| `UseProductionSettings()` | `AsBackgroundJob = true`, retention `30 days`, interval `4 h + 1–58 min jitter` |
| `UseTestSettings()` | `AsBackgroundJob = false`, retention `TimeSpan.MaxValue` (never drop), `ExecutionInterval = 0` |

There is no `WithExecutionInterval` for cleanup — set the property directly if you need a custom interval.

### Example

```csharp
settings.CleanupSettings
    .RunAsJob()
    .WithRetentionDays(14);
settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(2);
```

---

## PgOutboxConsumeSettings

Per-consumer-group **minimum offsets** — a floor for the persisted offset of a consumer group (public, sealed). Internally it is a `Dictionary<string, Guid>` keyed by consumer group id.

| Member | Description |
|---|---|
| `WithMinOffset(string consumerGroupId, Guid offset)` | Sets the minimum offset for the group (upsert — calling twice for the same group replaces the value). Expects an actual UUID v7 |
| `WithMinOffset(string consumerGroupId, DateTimeOffset offset)` | Converts the date-time to a UUID v7 (`Guid.CreateVersion7(offset)`) and then to its **minimum form** via `ToMinGuidV7()` — the random bits of the v7 GUID are zeroed, so the result is the smallest possible v7 GUID for that timestamp (a clean millisecond boundary) |
| `GetMinOffset(string consumerGroupId)` | Returns the configured minimum, or `Guid.Empty` if none was set for the group |

### Where it is applied — `OutboxTaskLoader`

For each consumer group + tenant the loader:

1. takes an advisory lock for the group,
2. reads the persisted offset from the `__offset$` table (`SELECT group_offset WHERE consumer_group = ... AND tenant_id = ...`); if no row exists yet, inserts one initialized with the configured minimum offset (or `Guid.Empty`),
3. computes the effective offset as `max(persisted offset, GetMinOffset(group))` — the minimum is a **floor**: the persisted offset can only move the consumer forward, never below the configured boundary,
4. loads the batch with `... WHERE msg_id > @offset ... ORDER BY msg_id LIMIT n` — since `msg_id` is a UUID v7, the offset is effectively "skip every message created before this time boundary",
5. advances the persisted offset to the max `msg_id` of the loaded batch.

Practical effect: after an offset loss, a re-deploy, or on a group's first start, `WithMinOffset(group, DateTimeOffset.UtcNow)` makes the consumer start "from now" instead of replaying the whole historical backlog.

### Example

```csharp
settings.ConsumeSettings
    .WithMinOffset("cg_order_consumer", DateTimeOffset.UtcNow)  // skip everything published before now
    .WithMinOffset("cg_analytics", Guid.Parse("01913a2e-8f3c-7a1e-8f3c-0a2b3c4d5e6f"));
```

---

## OutboxFieldDefaults

Static class with the default column names used by every `Fields` property. Grouped by the table they belong to:

| Group | Constant | Default value |
|---|---|---|
| Common (message) | `MsgId` | `msg_id` |
| Common (message) | `TenantId` | `tenant_id` |
| Common (message) | `MsgPart` | `msg_part` |
| Common (message) | `MsgPayloadId` | `msg_payload_id` |
| Common (message) | `MsgPayloadType` | `msg_payload_type` |
| Common (message) | `MsgPayload` | `msg_payload` |
| Common (message) | `MsgPayloadSize` | `msg_payload_size` |
| Common (message) | `MsgCreatedAt` | `msg_created_at` |
| TaskQueue (`outbox`) | `TaskId` | `task_id` |
| TaskQueue (`outbox`) | `ConsumerGroup` | `consumer_group` |
| TaskQueue (`outbox`) | `TaskLockExpiresOn` | `task_lock_expires_on` |
| TaskQueue (`outbox`) | `TaskTransactId` | `task_transact_id` |
| TaskQueue (`outbox`) | `TaskCreatedAt` | `task_created_at` |
| Delivery (`__log$`) | `DeliveryId` | `delivery_id` |
| Delivery (`__log$`) | `DeliveryAttempt` | `delivery_attempt` |
| Delivery (`__log$`) | `DeliveryStatusCode` | `delivery_status_code` |
| Delivery (`__log$`) | `DeliveryStatusMessage` | `delivery_status_message` |
| Delivery (`__log$`) | `DeliveryCreatedAt` | `delivery_created_at` |
| Error (`__error$`) | `ErrorId` | `error_id` |
| Error (`__error$`) | `ErrorType` | `error_type` |
| Error (`__error$`) | `ErrorMessage` | `error_message` |
| Error (`__error$`) | `ErrorCreatedAt` | `error_created_at` |
| Type (`__type$`) | `TypeId` | `type_id` |
| Type (`__type$`) | `TypeName` | `type_name` |
| Offset (`__offset$`) | `GroupOffset` | `group_offset` |
| Offset (`__offset$`) | `GroupUpdatedAt` | `group_updated_at` |

"Common (message)" constants are shared by several tables (the same `msg_id` column exists in the message, task queue, and delivery tables); `ConsumerGroup`, `TenantId`, `TaskId`, `TaskLockExpiresOn`, `TaskTransactId`, `TaskCreatedAt`, and `ErrorId` are shared between the task queue / delivery / offset tables as listed above.

---

## OutboxMessageSerializer

The default message serializer (internal, sealed). It implements the public `IOutboxMessageSerializer` interface (`Serialize<T>(Stream, T)` / `Deserialize<T>(Stream)`) by calling `System.Text.Json`'s `JsonSerializer.Serialize` / `Deserialize` directly — i.e. reflection-based, generic, type-agnostic JSON. The AOT warnings (`IL2026` / `IL3050`) are explicitly suppressed, which is why a single shared `OutboxMessageSerializer.Instance` is registered by `WithDefaultSerializer()` and works for any message type.

When to replace it: (a) you are publishing with **Native AOT** and want source-generated JSON (`[JsonSourceGenerationOptions]` + `JsonSerializerContext`) instead of reflection; (b) you need a non-JSON wire format (e.g. ProtoBuf, MessagePack); (c) you need custom options (naming policy, polymorphism, converters). Replace it with any of the three `WithMessageSerializer(...)` overloads — see the `IPgOutboxConfiguration` section. Note the class itself is `internal`, so user code can only reference the shared instance from code with `InternalsVisibleTo` access; normal consumers just omit `WithMessageSerializer(...)` (default JSON) or supply their own serializer type/instance/factory.

---

## Usage examples

### 1. Custom schema, base table name, and retention

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        // schema + all six table names in one call
        settings.TableSettings
            .UseBaseTableName("outbox")
            .WithSchema("my_outbox");

        // fill factors
        settings.TableSettings.TaskQueue.FillFactor = 65;
        settings.TableSettings.Message.FillFactor = 100;

        // partition maintenance
        settings.MigrationSettings
            .RunAsJob()
            .WithExecutionInterval(TimeSpan.FromHours(6));
        settings.MigrationSettings.ForwardDays = 3;

        settings.CleanupSettings
            .RunAsJob()
            .WithRetentionDays(30);
        settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(4);
    })
);
```

### 2. Disabling background migration

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        settings.MigrationSettings.AsBackgroundJob = false;  // job is Disabled()
        // IMigrationService stays resolvable — run migrations manually:
        // var migration = host.Services.GetRequiredService<IMigrationService>();
        // await migration.WaitMigration(TimeSpan.FromSeconds(30), ct);
    })
);
```

### 3. Minimum offset + custom serializer

```csharp
// your serializer: stateless, parameterless constructor
public sealed class OrderMessageSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream) => JsonSerializer.Deserialize<T>(stream);
    public void Serialize<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value);
}

services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
    .WithOutboxSettings((_, settings) =>
    {
        // new consumer group starts "from now" — no historical backlog replay
        settings.ConsumeSettings
            .WithMinOffset("cg_order_consumer", DateTimeOffset.UtcNow)
            .WithMinOffset("cg_analytics", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    })
    // removes the default JSON serializer and registers yours
    .WithMessageSerializer<OrderMessageSerializer>()
);
```

---

Full quick start — see the package Readme: `src/Sa.Outbox.PostgreSql/Readme.md`.
