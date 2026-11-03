# Configuration

**English** | [Русский](Readme-ru.md)

## Contents

- [Overview](#overview)
- [Registration flow (DI)](#registration-flow-di)
- [IPgOutboxConfiguration](#ipgoutboxconfiguration)
- [PgOutboxSettings](#pgoutboxsettings)
- [PgOutboxTableSettings](#pgoutboxtablesettings)
- [PgOutboxMigrationSettings](#pgoutboxmigrationsettings)
- [PgOutboxCleanupSettings](#pgoutboxcleanupsettings)
- [PgOutboxConsumeSettings](#pgoutboxconsumesettings)
- [PgOutboxDeliveryOptions](#pgoutboxdeliveryoptions) — per-group settings from a section
- [PgPollingSettings](#pgpollingsettings) — polling profiles and budgeting
- [OutboxFieldDefaults](#outboxfielddefaults)
- [Serialization](#serialization)
- [Usage examples](#usage-examples)
- [Cheat sheet: what to change where](#cheat-sheet-what-to-change-where)

## Cheat sheet: what to change where

| Task | Where |
|---|---|
| Connection, pool, `Search Path` | `WithDataSource(ds => ds.Options(...))` |
| Schema, table names, fill factors, migration, cleanup | `WithOutboxSettings(...)` (code) or `WithOutboxSettingsFromConfiguration("Sa:Outbox:Pg")` (appsettings) |
| One group's settings: batch, locks, attempts, intervals | section `Outbox:DeliveryGroups:{group}` or the `AddDelivery` callback |
| Polling latency for every group | `WithPollingBudget(...)`, `WithPollingProfile(...)`, `WithPollingFromConfiguration(...)` |
| Tenants | `WithTenants(...)` in `AddSaOutbox` / `AddSaOutboxUsingPostgreSql(configureOutbox: ...)` |
| Serializer | `WithMessageSerializer(...)` — in the core (shared) or in the PG provider |
| Permanent-failure handler | `WithPoisonHandler(...)` |
| Runtime: pause, live changes | `IOutboxConsumerManager` (`Pause` / `Resume` / `Apply`) |
| A custom schedule scope | one `scopeKey` in `AddSaOutboxUsingPostgreSql(pg, outbox, scopeKey)` |

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
var configuration = new PgOutboxConfiguration(services);

configuration.WithOutboxSettings();   // ①
configuration.WithDataSource();       // ②

configure?.Invoke(configuration);     // ③ user delegate — always runs last
```

### Serializer fallback

Serialization is a core concern: `AddSaOutbox` registers
`Sa.Outbox.Serialization.JsonOutboxMessageSerializer` via `TryAddSingleton`, and the provider resolves
`Sa.Outbox.Serialization.IOutboxMessageSerializer` from DI. After the user delegate,
`AddPgOutboxSettings` adds a compatibility bridge: when a serializer was registered under the legacy
provider-specific `Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer` type and no core
serializer exists, the core service resolves that instance; otherwise it falls back to the JSON
serializer. A serializer registered earlier in your code (or by another provider) also wins over the
fallback.

### ① Outbox settings

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

### ② Data source

`WithDataSource(configure)` delegates to `AddSaPostgreSqlDataSource(configure)` from `Sa.Data.PostgreSql`, which registers the data source on the standard `Microsoft.Extensions.Options` pipeline:

- The configuration surface is the flat options type `PgDataSourceOptions` — its single property is `ConnectionString`; pooling, timeout, and search path live inside the string itself.
- The bare call in step ② (no callback) is idempotent: `AddOptions<T>` registers its infrastructure via `TryAdd`, and the normaliser / validator / factory registrations are deduplicated via `TryAddEnumerable`, so a repeated bare call adds nothing. The step ③ `WithDataSource(configure)` call is the first *configuring* call — it is the one that adds the `Configure` callbacks / section binding — and it runs last, so it has the last word on the options value.
- `IPgDataSource` is registered as a singleton; its factory reads `IOptions<PgDataSourceOptions>.Value` (which runs the validators) at first resolve: a non-empty `ConnectionString` builds an owned `NpgsqlDataSource`; an empty string falls back to an `NpgsqlDataSource` registered in DI (sharing its connection pool — the wrapper does not dispose it); neither available → `OptionsValidationException`.
- The `Search Path` key of the connection string (or `"public"` when absent or unparseable) is the source for the schema auto-inheritance in step ①.

### ③ User delegate

Your `configure` delegate runs **last** (after all defaults). Its three public methods:

- `WithMessageSerializer(...)` — removes every existing core `Sa.Outbox.Serialization.IOutboxMessageSerializer` registration (`RemoveAll`) and adds exactly one new one (`TryAddSingleton`), so it always wins over the core JSON fallback. The legacy provider-specific interface is accepted as a subtype, so an existing implementation keeps working.
- `WithOutboxSettings(userAction)` — appends your delegate to the registered list of `Action<IServiceProvider, PgOutboxSettings>`; it will run after any previously registered delegate.
- `WithDataSource(configure)` — delegates to `AddSaPostgreSqlDataSource(configure)`. The step ② call is bare, so this is the first *configuring* call: its `Configure` callbacks / section binding (`Options(...)` / `FromConfiguration(...)` in the same delegate) are what actually shape `PgDataSourceOptions`. A second configuring call on the same service collection (e.g. a direct `services.AddSaPostgreSqlDataSource(configure)` earlier in your code) throws `InvalidOperationException`, because both `Configure` callbacks would otherwise stack onto the same unnamed options instance and the settings would silently merge.

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
| `WithOutboxSettingsFromConfiguration(string)` *(extension)* | Bind the scalar part of `PgOutboxSettings` from an `IConfiguration` section (see `PgOutboxOptions`) |
| `WithPolling(Action<IServiceProvider, PgPollingSettings>?)` | Register polling defaults for every group (see `PgPollingSettings`); without this call the service is not registered at all |
| `WithPollingBudget(TimeSpan)` / `WithPollingProfile(PgPollingProfile)` *(extensions)* | Set the same profile from one number or a preset |
| `WithPollingFromConfiguration(string)` *(extension)* | Bind the polling profile from an `IConfiguration` section (see `PgPollingOptions`) |
| `WithDataSource(Action<IDataSourceBuilder>?)` | Configure the PostgreSQL data source (connection string): the section via `FromConfiguration`, the options pipeline via `Options(...)` |

All methods return the same `IPgOutboxConfiguration` for chaining.

### `WithMessageSerializer` — three overloads

Each overload first removes all existing core `IOutboxMessageSerializer` registrations (`RemoveAll`) and then adds exactly one, so any overload always replaces the core JSON fallback. The serializer is the shared core service: a serializer set here is also used by another provider in the same container.

| Overload | DI registration | When to use |
|---|---|---|
| `Func<IServiceProvider, IOutboxMessageSerializer>` | `TryAddSingleton` with a factory | The serializer depends on DI — e.g. reads options from `IConfiguration`, or obtains a source-generated `JsonSerializerContext` from the container |
| `TService instance` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton` with the instance | You already have a pre-built serializer (custom options, injected dependencies in the constructor); it is used directly, never resolved from DI |
| `TService` (`TService : class, IOutboxMessageSerializer`) | `TryAddSingleton<IOutboxMessageSerializer, TService>` | Simple stateless serializer with a parameterless constructor. The XML doc calls this "transient", but the code registers a **singleton** — the same instance is reused for the container's lifetime |

### `WithOutboxSettings`

Takes an optional `Action<IServiceProvider, PgOutboxSettings>`. When the delegate is non-null it is stored as a singleton and later applied inside the `PgOutboxSettings` factory (see Registration flow ①). The `IServiceProvider` parameter is available if you need to read other registered services while configuring (e.g. `IConfiguration`).

### `WithDataSource`

Takes an optional `Action<IDataSourceBuilder>` callback and delegates it to `AddSaPostgreSqlDataSource(...)` from `Sa.Data.PostgreSql` — the section comes via `FromConfiguration(...)`, the options pipeline via `Options(...)`; its [readme](../../../Data/Sa.Data.PostgreSql/Readme.md) covers the full pipeline (normalisation, validation, `ValidateOnStart()`). The only option is `ConnectionString`; pooling and other Npgsql options are configured inside the string itself (e.g. `Minimum Pool Size`, `Maximum Pool Size`, `Search Path`, `Timeout`). `Search Path` additionally drives the automatic schema inheritance of the outbox tables (step ①); an empty string reuses an `NpgsqlDataSource` registered in DI instead of opening a second pool.

### `WithPolling` and the presets

Sets **polling defaults for every consumer group** of the provider without repeating a `configure` callback on each `AddDelivery`. These are defaults, not overrides: a group's section and its callback are applied later and win.

| Method | What it sets |
|---|---|
| `WithPollingBudget(TimeSpan)` | Everything from one number: the "published → processing started" SLA for an idle group |
| `WithPollingProfile(PgPollingProfile)` | A ready-made `Quiet` / `Balanced` / `Responsive` preset |
| `WithPolling((sp, s) => ...)` | Manual tuning of `PgPollingSettings` (including `WithBatchingWindow`, `WithPerTenantParallelism`, `Disable`) |
| `WithPollingFromConfiguration(path)` | Profile/budget/explicit values from an `IConfiguration` section (`PgPollingOptions`) |

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds.Options(o => o.Configure(x => x.ConnectionString = cs)))
    .WithPollingBudget(TimeSpan.FromMinutes(1)));   // ≤ 1 min for every group
```

```jsonc
// appsettings.json — change the latency without a rebuild
{
  "Sa": {
    "Outbox": {
      "Pg": {
        "Polling": {
          "Budget": "00:01:00",
          // or "Profile": "Quiet",
          "Interval": "00:00:10"   // an explicit value wins over the profile/budget
        }
      }
    }
  }
}
```

```csharp
.WithPollingFromConfiguration("Sa:Outbox:Pg:Polling")
```

> If none of these methods is called, no `PgPollingSettings` is registered and the provider's behaviour is exactly the same as before (core `OutboxDefaults` + section + callback).

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

### Settings from configuration — `PgOutboxOptions`

The scalar part can live in `appsettings.json` instead of code:

```jsonc
{
  "Sa": {
    "Outbox": {
      "Pg": {
        "Schema": "messaging",
        "Tables": {
          "TaskQueue": { "TableName": "outbox", "FillFactor": 70 },
          "Message":   { "TableName": "outbox__msg$" }
        },
        "Migration": { "ForwardDays": 3, "ExecutionInterval": "06:00:00" },
        "Cleanup":   { "DropPartsAfterRetention": "30.00:00:00", "CleanupMode": "Drop" }
      }
    }
  }
}
```

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(...)
    .WithOutboxSettingsFromConfiguration("Sa:Outbox:Pg"))
```

Only the scalar surface is bound: `Schema`, `UseDefaultPartition`, the six table names and fill factors, and the migration/cleanup schedules. Column definitions (`Fields.*`) stay in code — they are raw SQL fragments spliced into DDL verbatim. A missing section is a no-op; an unusable value (for example a `FillFactor` outside 10–100) fails at startup during validation.

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

#### Message table (`__msg$`) — 9 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `MsgSeq` | `msg_seq` | `BIGSERIAL NOT NULL` | Database-assigned insertion order (C2) — the consumption cursor lives on this, never on the v7 id |
| `MsgId` | `msg_id` | `UUID NOT NULL` | Message id — UUID v7, generated by the application; the partition sort key |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant id; first partitioning key |
| `MsgPart` | `msg_part` | `TEXT NOT NULL` | Message part; second partitioning key |
| `MsgPayloadId` | `msg_payload_id` | `TEXT NOT NULL DEFAULT ''` | Optional payload id supplied by the publisher |
| `MsgPayloadType` | `msg_payload_type` | `BIGINT NOT NULL DEFAULT 0` | Hash of the payload type (murmurHash3), matching the `__type$` registry |
| `MsgPayload` | `msg_payload` | `BYTEA NOT NULL` | Serialized message payload |
| `MsgPayloadSize` | `msg_payload_size` | `INT NOT NULL DEFAULT 0` | Payload size in bytes |
| `MsgCreatedAt` | `msg_created_at` | `BIGINT NOT NULL DEFAULT 0` | Creation time (ticks); partition timestamp column |

#### Task queue table (`outbox`) — 18 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `TaskId` | `task_id` | `BIGSERIAL NOT NULL` | Task surrogate id |
| `ConsumerGroup` | `consumer_group` | `TEXT NOT NULL` | Consumer group; second partitioning key |
| `TaskLockExpiresOn` | `task_lock_expires_on` | `BIGINT NOT NULL DEFAULT 0` | Lock TTL (ticks) — consumers competing with `SKIP LOCKED` skip rows whose lock is still valid |
| `TaskTransactId` | `task_transact_id` | `TEXT NOT NULL DEFAULT ''` | Transaction / correlation id of the worker processing the task |
| `MsgSeq` | `msg_seq` | `BIGINT NOT NULL DEFAULT 0` | Source `msg_seq` (denormalized) — loaded with the task, returned by `SqlLoadConsumerGroup` as the new cursor |
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

#### Offset table (`__offset$`) — 5 columns

| Property | Default name | Column definition | Description |
|---|---|---|---|
| `ConsumerGroup` | `consumer_group` | `TEXT` (nullable) | Consumer group id — part of the primary key |
| `TenantId` | `tenant_id` | `INT NOT NULL DEFAULT 0` | Tenant — part of the primary key |
| `GroupOffset` | `group_offset` | `UUID NOT NULL DEFAULT '{Guid.Empty}'` | Legacy cursor (UUID v7) — kept for rolling upgrades, never read by this version, dropped by a later contract version |
| `GroupOffsetSeq` | `group_offset_seq` | `BIGINT NOT NULL DEFAULT 0` | Live cursor (C2) — the last consumed `msg_seq`; read and advanced only via this column |
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

Per-consumer-group **minimum offsets** — a floor for the persisted offset of a consumer group (public, sealed). The public API is unchanged (UUID v7 / `DateTimeOffset`), but the cursor is now a `msg_seq` (BIGINT), so each floor is translated on first use:

- the raw floor (`Guid` v7 or `DateTimeOffset`) is stored in a `Dictionary<string, ...>` keyed by consumer group id;
- on the first load inside the advisory-lock transaction the loader resolves it to an **exclusive `msg_seq` boundary** — `MAX(msg_seq)` of the messages that still sort below the floor (0 when none do) — and caches the result per (group, tenant), so the translation cost is paid once. The resolving queries filter by `tenant_id`, so the boundary is tenant-specific; a group-only cache entry used to hand the first tenant's boundary to every other tenant.

| Member | Description |
|---|---|
| `WithMinOffset(string consumerGroupId, Guid offset)` | Sets the floor for the group (upsert — calling twice for the same group replaces the value). Expects an actual UUID v7; resolved as "skip every message whose `msg_id` sorts below this id" |
| `WithMinOffset(string consumerGroupId, DateTimeOffset offset)` | Sets the floor from a timestamp; resolved as "skip every message whose `msg_created_at` is before this instant" (second precision — the timestamp is stored as unix seconds) |

The former `GetMinOffset` reader is gone — it exposed the pre-C2 UUID cursor and has no meaning once the floor is resolved to a `msg_seq`.

### Where it is applied — `OutboxTaskLoader`

For each consumer group + tenant the loader:

1. takes an advisory lock for the group,
2. reads the persisted cursor from the `__offset$` table (`SELECT group_offset_seq WHERE consumer_group = ... AND tenant_id = ...`); if no row exists yet, inserts one initialized to the resolved floor (`0` when no floor is configured),
3. resolves (on first use per run) and caches the configured floor to its exclusive `msg_seq` boundary — per (group, tenant); see above,
4. computes the effective offset as `max(persisted offset, floor)` — the floor can only move the consumer forward, never below the configured boundary,
5. loads the batch with `... WHERE msg_seq > @offset ... ORDER BY msg_seq LIMIT n` — `msg_seq` is assigned by the database at COPY time, so a backdated or skewed application clock can never make a new message sort below the advanced cursor,
6. advances the persisted cursor to the max `msg_seq` of the loaded batch (returned by the load).

Practical effect: after an offset loss, a re-deploy, or on a group's first start, `WithMinOffset(group, DateTimeOffset.UtcNow)` makes the consumer start "from now" instead of replaying the whole historical backlog. Note the floor is evaluated against `msg_created_at` as recorded in the database — publish timestamps that are older than the floor are intentionally skipped even if they arrive late.

### Example

```csharp
settings.ConsumeSettings
    .WithMinOffset("cg_order_consumer", DateTimeOffset.UtcNow)  // skip everything published before now
    .WithMinOffset("cg_analytics", Guid.Parse("01913a2e-8f3c-7a1e-8f3c-0a2b3c4d5e6f"));
```

---

## PgOutboxDeliveryOptions

Per-consumer-group delivery settings bound from a single `IConfiguration` section — the section is addressed by the `configurationKey` passed to `AddDelivery(..., configurationKey: ...)` / `AddDeliveryScoped(..., configurationKey: ...)`. Public, sealed.

### Precedence

Each group's final `OutboxConsumerSettings` is composed in four layers, lowest → highest:

```
OutboxDefaults  →  PgPollingSettings (only with WithPolling*)  →  the group's section  →  the configure callback
```

**Which section.** By convention a group reads `Outbox:DeliveryGroups:{groupId}` (the prefix is changed by `IOutboxBuilder.WithDeliveryConfigurationSection`; `null` disables the convention). An explicit `configurationKey` on `AddDelivery` wins over the convention, and an explicit empty string (`configurationKey: ""`) opts that group out. A missing section is a no-op.

- **`PgPollingSettings`** is applied first, and only for the properties it sets (`Interval`, `IdleMaxInterval`, `InitialDelay`, `MaxProcessingIterations` and — only when set explicitly — `BatchingWindow` / `PerTenantMaxDegreeOfParallelism`). Without `WithPolling*` the layer does not exist at all.
- A **partially-filled** section overrides only the properties it names; every other property keeps whatever the builder already carries (`OutboxDefaults`, a `PgPollingSettings` value, or an earlier override).
- A group **without** a `configurationKey` reads the conventional section `Outbox:DeliveryGroups:{groupId}` (when it exists) and is shaped by `OutboxDefaults` + `PgPollingSettings` + `configure`; sections under other prefixes are not read.
- An **absent** or **present-but-empty** section is a no-op (every property binds to null → nothing is applied).

### Bindable properties

All 16 `OutboxConsumerSettings` scalars are bindable and **nullable** — `null` means "not specified, keep current":

| Property | Type | Notes |
|---|---|---|
| `Interval` | `TimeSpan?` | Job execution period |
| `InitialDelay` | `TimeSpan?` | Delay before the first run after startup |
| `ConcurrencyLimit` | `int?` | Group-level schedule concurrency limit |
| `MaxConcurrency` | `int?` | Max simultaneously running jobs within the group |
| `RetryCountOnError` | `int?` | Job retries on error (`WithInfiniteRetries()` = `int.MaxValue`; negative values are rejected) |
| `MaxBatchSize` | `int?` | Upper bound for the rented batch size |
| `MaxProcessingIterations` | `int?` | Iterations per cycle (`-1` = drain until empty) |
| `IterationDelay` | `TimeSpan?` | Delay between processing iterations within a cycle |
| `LockDuration` | `TimeSpan?` | Lock TTL for a rented task |
| `LockRenewal` | `TimeSpan?` | Lock renewal interval |
| `LookbackInterval` | `TimeSpan?` | How far back to scan for unprocessed messages |
| `MaxDeliveryAttempts` | `int?` | Delivery-attempt budget before dead-lettering |
| `BatchingWindow` | `TimeSpan?` | Window for accumulating messages into a batch |
| `PerTenantTimeout` | `TimeSpan?` | Per-tenant processing timeout (`0` = none) |
| `PerTenantMaxDegreeOfParallelism` | `int?` | Per-tenant parallelism (`1` = sequential, `-1` = one per CPU) |
| `IdleMaxInterval` | `TimeSpan?` | Upper bound for the adaptive empty-queue back-off (`0` = disabled) |

`TimeSpan` values bind from the standard `HH:mm:ss` form (`00:00:30`); `int` from a plain integer.

### Deliberately excluded

Two `OutboxConsumerSettings` members are **not** bindable, and on purpose:

- **`Paused`** — runtime state, controlled at runtime via `IOutboxConsumerManager.Pause` / `Resume`, not configuration.
- **`AsSingleton`** — the lifetime contract of the registration: `AddDelivery` registers a keyed *singleton* consumer, `AddDeliveryScoped` a keyed *scoped* consumer. It is fixed by which method you call; the group identity is likewise pinned to the explicit `consumerGroupId` argument.

### How it is applied

`PgOutboxDeliveryOptionsProvider` — the `IDeliveryGroupOptionsProvider` this package registers (replacing the base no-op) — resolves the `IConfiguration` and `PgPollingSettings` once at construction (both optional: no `IConfiguration`, or no `WithPolling*` call, simply means the corresponding layer is absent), then per delivery group:

1. `PgPollingSettings.ApplyTo(builder)` — the provider-wide polling defaults (only when `Enabled`); applied **before** the section, so the section and the callback override them.
2. `PgOutboxDeliveryOptions.FromConfiguration(configuration, configurationKey)` — returns `null` when the key is empty or the section is absent; otherwise binds the section via the source-generated `IConfigurationSection.Bind` (AOT-safe, the same choice as the partitional PostgreSql settings).
3. `PgOutboxDeliveryOptions.ApplyTo(builder)` — applies every non-null property to the settings builder, leaving nulls untouched; `IdleMaxInterval == 0` maps to `WithFixedInterval()`, a positive value to `WithIdleBackoff(...)`.

The provider is consulted only in the **stage-2 (startup)** settings build — the stage-1 registration-time snapshot is pre-registered first (it cannot see the section, since `IConfiguration` needs a live provider).

### Example

```jsonc
// appsettings.json
{
  "DeliveryGroups": {
    "cg_order_consumer": {
      "MaxBatchSize": 64,
      "Interval": "00:00:30",
      "IdleMaxInterval": "00:00:30",
      "MaxDeliveryAttempts": 5
    }
  }
}
```

```csharp
services.AddSaOutbox(builder => builder
    .WithDeliveries(b => b
        .AddDelivery<OrderConsumer, OrderCreated>(
            "cg_order_consumer",
            (_, settings) => settings.StartImmediately(),   // the final word over the section
            configurationKey: "DeliveryGroups:cg_order_consumer")));
```

Here `MaxBatchSize` / `Interval` / `IdleMaxInterval` / `MaxDeliveryAttempts` come from the section (over `OutboxDefaults`), while `StartImmediately()` from the callback is the final word on `InitialDelay`.

---

## PgPollingSettings

Provider-wide **polling defaults** for every consumer group that does not override them. Registered by `WithPolling` / `WithPollingProfile` / `WithPollingBudget` (see `IPgOutboxConfiguration`); without such a call the `PgPollingSettings` service is absent from the container and the provider behaves as before.

There is no reactivity here: it is plain periodic polling. The worst case for an idle group is

```
BatchingWindow + IdleMaxInterval + drain time
```

so the latency budget is split with a reserve: `BatchingWindow (3 s) + reserve + IdleMaxInterval`, where `reserve = max(5 s, 20% of the budget)`.

### Named profiles (`WithPollingProfile`)

| Profile | `Interval` | `IdleMaxInterval` | `InitialDelay` | `MaxProcessingIterations` | Worst case |
|---|---|---|---|---|---|
| `Quiet` | 30 s | 45 s | 5 s | −1 | ~48 s |
| `Balanced` (default) | 10 s | 30 s | 5 s | −1 | ~33 s |
| `Responsive` | 3 s | 15 s | 3 s | −1 | ~18 s |

### Budgeting (`WithPollingBudget`)

The budget is the upper bound of "published → processing started" for an **idle** group. It is split into three parts:

```
budget   = BatchingWindow (3 s) + reserve + IdleMaxInterval
reserve  = max(5 s, 20% of the budget)      // room for draining, many tenants and back-off jitter
Interval = max(1 s, IdleMaxInterval / 3)    // while there is work, poll three times as often
```

| Budget | `Interval` | `IdleMaxInterval` | `InitialDelay` | Worst case |
|---|---|---|---|---|
| 30 s | 7 s | 21 s | 5 s | ~24 s |
| 60 s | 15 s | 45 s | 5 s | ~48 s |
| 120 s | 31 s | 93 s | 5 s | ~96 s |

`MaxProcessingIterations` always becomes `-1`: a burst is drained in the same cycle instead of waiting for the next tick. A budget below `BatchingWindow + reserve` (i.e. ≤ 8 s with the default window) is rejected during validation. The budget does not change `BatchingWindow` itself — the window stays whatever the group or `OutboxDefaults` set.

#### Example: a one-minute budget with one exception

```csharp
services
    .AddSaOutbox(b => b.WithDeliveries(d => d
        .AddDelivery<OrderConsumer, OrderCreated>("orders")            // the budget defaults
        .AddDelivery<AuditConsumer, AuditEvent>("audit", (_, cs) => cs // exception: SLA matters more
            .WithInterval(TimeSpan.FromSeconds(2))
            .WithFixedInterval())))
    .AddSaOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(...)
        .WithPollingBudget(TimeSpan.FromMinutes(1)));
```

The resulting group settings — the configuration section and the callback are applied after the budget and therefore override it:

| Group | `Interval` | `IdleMaxInterval` | `InitialDelay` | `MaxProcessingIterations` | Source |
|---|---|---|---|---|---|
| `orders` | 15 s | 45 s | 5 s | −1 | `WithPollingBudget(1 min)` |
| `audit` | 2 s | 0 (fixed) | 5 s | −1 | the callback overrode `Interval` and `IdleMaxInterval` |

The same result for `audit` can be expressed in configuration, without a callback:

```jsonc
{
  "DeliveryGroups": {
    "audit": { "Interval": "00:00:02", "IdleMaxInterval": "00:00:00" }
  }
}
```

> The budget is a **default, not a hard limit**: once a group overrides `Interval` / `IdleMaxInterval`, its worst case is derived from its own values.

### Manual tuning (`WithPolling`)

```csharp
.WithPolling((_, polling) => polling
    .UseProfile(PgPollingProfile.Balanced)
    .WithPerTenantParallelism(4)                      // many tenants behind one group
    .WithBatchingWindow(TimeSpan.FromSeconds(1)))     // deliberate: affects Warn retries too
```

### Deliberately untouched

- `BatchingWindow` — `null` by default: the window is also the retry delay of an un-backoff-ed `Warn`, so it must not be changed "for free".
- `LockDuration` / `LockRenewal` / `LookbackInterval` / `MaxBatchSize` / `MaxDeliveryAttempts` — per-group decisions with their own defaults.
- `PerTenantMaxDegreeOfParallelism` — only set explicitly (`WithPerTenantParallelism`).
- `Paused` / `AsSingleton` — not polling settings (see `PgOutboxDeliveryOptions`).

Every value is validated when `PgPollingSettings` is first resolved: `Interval > 0`, `IdleMaxInterval ≥ Interval` (when enabled), `InitialDelay ≥ 0`, `MaxProcessingIterations ≥ -1`.

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
| Common (message) | `MsgSeq` | `msg_seq` |
| Common (message) | `MsgCreatedAt` | `msg_created_at` |
| TaskQueue (`outbox`) | `TaskId` | `task_id` |
| TaskQueue (`outbox`) | `ConsumerGroup` | `consumer_group` |
| TaskQueue (`outbox`) | `TaskLockExpiresOn` | `task_lock_expires_on` |
| TaskQueue (`outbox`) | `TaskTransactId` | `task_transact_id` |
| TaskQueue (`outbox`) | `MsgSeq` | `msg_seq` |
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
| Offset (`__offset$`) | `GroupOffsetSeq` | `group_offset_seq` |
| Offset (`__offset$`) | `GroupUpdatedAt` | `group_updated_at` |

"Common (message)" constants are shared by several tables (the same `msg_id` column exists in the message, task queue, and delivery tables); `MsgSeq` exists on both the message and task queue tables; `ConsumerGroup`, `TenantId`, `TaskId`, `TaskLockExpiresOn`, `TaskTransactId`, `TaskCreatedAt`, and `ErrorId` are shared between the task queue / delivery / offset tables as listed above.

---

## Serialization

Serialization lives in the core (`Sa.Outbox.Serialization`) and is shared by every provider:

- `IOutboxMessageSerializer` — the contract (`Serialize<T>(Stream, T)` / `Deserialize<T>(Stream)`).
- `JsonOutboxMessageSerializer` — the default, reflection-based UTF-8 JSON (AOT warnings
  `IL2026`/`IL3050` suppressed), registered by `AddSaOutbox` via `TryAddSingleton`.
- `OutboxPayloadCodec` — the pooled-stream byte bridge for providers that store a payload as a
  self-contained array (not used by the PostgreSQL `COPY` path, which streams directly).

This provider resolves the core service and ships no serializer of its own. The
`Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer` interface is a compatibility subtype of
the core one: an existing implementation still compiles and still satisfies
`WithMessageSerializer(...)`, and a direct DI registration under it is bridged to the core service.

When to replace the default: (a) you are publishing with **Native AOT** and want source-generated
JSON (`[JsonSourceGenerationOptions]` + `JsonSerializerContext`) instead of reflection; (b) you need
a non-JSON wire format (e.g. ProtoBuf, MessagePack); (c) you need custom options (naming policy,
polymorphism, converters). Use any of the three `WithMessageSerializer(...)` overloads — see the
`IPgOutboxConfiguration` section — or configure the serializer once at the core level, where it
covers every provider.

---

## Usage examples

### 1. Custom schema, base table name, and retention

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
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
    .WithDataSource(ds => ds.Options(ob => ob.Configure(o => o.ConnectionString = "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
    .WithOutboxSettings((_, settings) =>
    {
        settings.MigrationSettings.AsBackgroundJob = false;  // job is Disabled()
        // IMigrationService stays resolvable — run migrations manually:
        // var migration = host.Services.GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
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
    .WithDataSource(ds => ds.Options(ob => ob.Configure(o => o.ConnectionString = "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
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

Full quick start — see the package readme: [`../Readme.md`](../Readme.md).
