# Setup walkthrough

**English** | [Русский](Readme-ru.md)

Step-by-step wiring of the PostgreSQL provider: tenants, consumer registration, connection, serialization, poison handling, table settings and manual job runs. Part of the [`Sa.Outbox.PostgreSql`](../Readme.md) documentation; the settings reference is in [`Configuration/Readme.md`](../Configuration/Readme.md).

## Contents

- [Turning the provider off](#turning-the-provider-off)
- [Registering the same key twice](#registering-the-same-key-twice)
- [1. Tenant Configuration](#1-tenant-configuration)
- [2. Consumer Registration](#2-consumer-registration)
- [Per-group Settings from Configuration (`configurationKey`)](#per-group-settings-from-configuration-configurationkey)
- [Consumer group settings (`OutboxConsumerSettingsBuilder`)](#consumer-group-settings-outboxconsumersettingsbuilder)
- [Latency budget for every group at once](#latency-budget-for-every-group-at-once)
- [Runtime settings management](#runtime-settings-management)
- [3. PostgreSQL Connection](#3-postgresql-connection)
- [4. Message Serialization](#4-message-serialization)
- [5. Poison Handler (permanent failures)](#5-poison-handler-permanent-failures)
- [6. Table Settings](#6-table-settings)
- [7. Running Jobs Manually](#7-running-jobs-manually)

One registration call or two:

| Call | Purpose |
|---|---|
| `AddSaOutbox(...)` | Core: tenants, consumers, schedules |
| `AddSaOutboxUsingPostgreSql(cfg)` | PostgreSQL: connection, tables, migration, cleanup |
| `AddSaOutboxUsingPostgreSql(cfg, outbox)` | **One call**: core + provider, `scopeKey` written once |

One registration instead of two:

```csharp
services.AddSaOutboxUsingPostgreSql(
    pg => pg.WithDataSource(...).WithPollingBudget(TimeSpan.FromMinutes(1)),
    outbox => outbox
        .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
        .WithDeliveries(d => d.AddDelivery<OrderConsumer, OrderCreated>("orders")));
```

> **Isolation.** The Outbox registers its internal `Sa.Schedule` schedule and its
> `Sa.Partitional.PostgreSql` module under the scope key `"outbox"`
> (`Sa.Outbox.OutboxScope.DefaultKey`), and never touches the application's default schedule or
> partitional module. Both entry points take an optional trailing `scopeKey` parameter. In the
> one-call form `AddSaOutboxUsingPostgreSql(cfg, outbox, scopeKey)` it is written once; when the core
> is registered separately, pass the same value to both calls. The reserved `"default"` key is
> rejected.

## Turning the provider off

`WithEnabled(false)` is a **registration** switch, not a runtime flag:

```csharp
services.AddSaOutboxUsingPostgreSql(pg => pg
    .WithEnabled(configuration.GetValue("Outbox:PostgreSql:Enabled", true))
    .WithDataSource(...));
```

The call still runs the configuration callback, but nothing is applied to the container: no settings,
no keyed data source (so `ValidateOnStart` cannot fail on an absent connection string), no serializer
or poison override, no consumer groups and no `Sa.Schedule` jobs. `Enabled` is `true` by default; a
disabled provider leaves no trace in DI, so the registration can stay in code for every environment
and be switched off by configuration. Providers registered under other keys are unaffected. In the
split form (`AddSaOutbox` + this call) the core still registers the consumer groups, so a disabled
provider makes the startup scope-key check fail and names those groups — skip `AddSaOutbox` too, or
use the one-call form.

## Registering the same key twice

A repeated `AddSaOutboxUsingPostgreSql(..., scopeKey)` for one key is tolerated: the provider
contracts, the startup validator, the migration guard, the consumer-group registrations and the
schedule deduplicate. The outbox-settings and polling callbacks of the last registration replace the
ones recorded before, so they run once — not once per registration. The **first** data-source
registration owns the connection — a second *outbox* call's data-source callback is ignored instead
of throwing, so register the connection once.

A data source another module registered under the same key is shared, not duplicated: a bare
`WithDataSource()` borrows it. `WithDataSource(configure)` is an explicit connection intent and is
applied to that key; if the other module already configured it, `AddSaPostgreSqlDataSource` reports
the duplicate instead of the outbox silently keeping the other connection.

## 1. Tenant Configuration

Where inside `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithTenants((_, ts) => ts
        .WithTenantIds(1, 2, 3)                         // Fixed list
        // .WithAutoDetect()                             // Discover from messages at runtime
        // .WithTenantSource(myTenantSource)             // Custom ITenantSource
        // .WithTenantProvider(ct => ...)                // Func<CancellationToken, ValueTask<int[]>>
    )
    // ...
)
```

The detector behind `WithAutoDetect()` is supplied by the provider package; to replace it, register
your own at the provider level (`AddOutboxProvider(..., p => p.WithTenantDetector<MyDetector>())`).
Per-tenant parallelism is a consumer-group setting: `WithPerTenantMaxDegreeOfParallelism(4)` on the
group's `OutboxConsumerSettingsBuilder` (see below).

## 2. Consumer Registration

Where inside `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithDeliveries(b => b
        // Simplest form — auto-named consumer group from type name
        .AddDeliveryScoped<GreetingConsumer, EmailSent>()

        // Custom consumer group name (allows multiple consumers for one message type)
        .AddDeliveryScoped<EmailAnalyticsConsumer, EmailSent>("analytics")

        // With inline settings
        .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, settings) =>
        {
            settings
                .WithInterval(TimeSpan.FromSeconds(5))
                .StartImmediately()                     // start immediately, don't wait first interval
                .WithMaxBatchSize(16)                   // max messages per batch
                .WithMaxDeliveryAttempts(3)             // stop retrying after N attempts
                .WithLockDuration(TimeSpan.FromSeconds(10))
                .WithLookbackInterval(TimeSpan.FromDays(7));
        })
    )
    // ...
)
```

## Per-group Settings from Configuration (`configurationKey`)

The explicit-`consumerGroupId` overloads of `AddDelivery` / `AddDeliveryScoped` take an optional `configurationKey` — the path of an `IConfiguration` section holding that group's settings (bound to `PgOutboxDeliveryOptions`; see [Configuration/Readme.md](../Configuration/Readme.md#pgoutboxdeliveryoptions)):

```csharp
.AddDeliveryScoped<OrderConsumer, OrderCreated>(
    "cg_order_consumer",
    (_, settings) => settings
        .StartImmediately()
        .WithMaxBatchSize(16),
    configurationKey: "DeliveryGroups:cg_order_consumer")
```

The group's settings are composed in three layers, lowest → highest:

```
OutboxDefaults  →  configuration section  →  configure callback
```

- **Which section** — by convention `Outbox:DeliveryGroups:{groupId}`; an explicit `configurationKey` wins over it, and `configurationKey: ""` opts the group out. The convention prefix is changed by `IOutboxBuilder.WithDeliveryConfigurationSection(...)`; `null` disables it.
- **Key given** — the section's non-null values land between the defaults and the `configure` callback; a partially-filled section overrides only what it names.
- **No key** — the group reads the conventional section when it exists; sections under other prefixes are not read.
- An absent or empty section is a no-op.
- `Paused` is runtime state (`IOutboxConsumerManager`), `AsSingleton` is fixed by which method you call, and the group identity is pinned to the explicit `consumerGroupId` argument — none of them comes from the section.

The section just has to be part of the `IConfiguration` registered in DI (e.g. appsettings.json under `Host.CreateDefaultBuilder`); the provider reads it lazily at startup, so editing the section and restarting is the configuration workflow.

## Consumer group settings (`OutboxConsumerSettingsBuilder`)

`OutboxConsumerSettingsBuilder` — the single fluent builder for all consumer group settings:

| Method | Default | Description |
|---|---|---|
| `WithInterval(span)` | 1 min | Polling interval |
| `WithInitialDelay(span)` | 10 s | Delay before the first run after startup |
| `StartImmediately()` | — | Start without waiting for the first interval |
| `WithConcurrencyLimit(n)` | 1 | Concurrent jobs in the group |
| `WithMaxConcurrency(n)` | 48 | Max simultaneously running jobs within the group (high — for cloud deployments with connection pooling; override for local/small deployments) |
| `WithRetryCountOnError(n)` | 0 | Job retries on error; `WithInfiniteRetries()` sets `int.MaxValue`, negative values are rejected |
| `WithNoRetries()` | — | Disable retries |
| `WithInfiniteRetries()` | — | Infinite retries |
| `WithMaxBatchSize(n)` | 128 | Max messages per batch |
| `WithMaxProcessingIterations(n)` | 10 | Iterations per cycle (`-1` = drain until empty) |
| `WithSingleIteration()` | — | One iteration (testing) |
| `WithUnlimitedIterations()` | — | Drain until empty |
| `WithIterationDelay(span)` | 0 s | Delay between iterations within a cycle |
| `WithIdleBackoff(span)` | — | Enable adaptive empty-queue back-off up to `maxInterval` (must be `>= Interval`) |
| `WithFixedInterval()` | — | Explicitly keep a fixed poll interval (no back-off) |
| `WithLockDuration(span)` | 10 s | Lock TTL for a rented message |
| `WithLockRenewal(span)` | 3 s | Lock renewal interval |
| `WithLookbackInterval(span)` | 7 days | How far back to scan for unprocessed messages |
| `WithMaxDeliveryAttempts(n)` | 3 | Stop retrying a message after N attempts |
| `WithBatchingWindow(span)` | 3 s | Window for accumulating messages into a batch |
| `WithNoBatchingWindow()` | — | Take everything available now |
| `WithPerTenantTimeout(span)` | 0 (none) | Per-tenant processing timeout |
| `WithPerTenantMaxDegreeOfParallelism(n)` | 1 (sequential) | Per-tenant parallelism (`-1` = one per CPU) |
| `WithTenantSequentialProcessing()` | 1 | Sequential processing per tenant |
| `WithTenantMaxParallelism()` | -1 | Max parallelism per tenant |
| `Paused(bool)` | false | Pause the consumer group |
| `Resumed()` | — | Unpause |
| `AsSingleton(bool)` | true | Singleton (one per cluster) vs scoped consumer |
| `WithTag(object)` | — | Free-form tag for the group (diagnostics) |

## Latency budget for every group at once

Instead of repeating `Interval` / `IdleMaxInterval` / `InitialDelay` on each `AddDelivery`, set them once on the provider. These are defaults — a group's configuration section and its callback still win:

```csharp
services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(...)
    .WithPollingBudget(TimeSpan.FromMinutes(1)));   // idle group: ~48 s worst case
// or one of: .WithPollingProfile(PgPollingProfile.Quiet / Balanced / Responsive)
```

`WithPollingBudget` splits the budget as `BatchingWindow (3 s) + reserve (max(5 s, 20%)) + IdleMaxInterval` and sets `MaxProcessingIterations = -1` so a burst is drained in the same cycle. See [`Configuration/Readme.md` → `PgPollingSettings`](../Configuration/Readme.md#pgpollingsettings) for the value tables. Nothing is registered unless one of these methods is called.

## Runtime settings management

Settings are automatically registered in `IOutboxConsumerManager` on first job startup. For runtime changes:

```csharp
var manager = host.Services.GetRequiredService<IOutboxConsumerManager>();

// Atomic swap — the new snapshot is applied on the next iteration
manager.Apply("cg_order_consumer", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("cg_order_consumer");
manager.Unpause("cg_order_consumer");

// Subscribe to changes
using var sub = manager.Subscribe("cg_order_consumer", updated =>
{
    // react to settings changes
});

// Check state
bool paused = manager.IsPaused("cg_order_consumer");
```

## 3. PostgreSQL Connection

Where inside `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .Options(ob => ob.Configure(o => o.ConnectionString =
            "Host=...;Database=...;Username=...;Password=...")
        // Pooling, timeout and search path live inside the connection string itself, e.g.:
        // "Host=...;Minimum Pool Size=5;Maximum Pool Size=100;Search Path=outbox"
    ))
    // ...
)
```

## 4. Message Serialization

`IOutboxMessageSerializer` lives in the core (`Sa.Outbox.Serialization`); the core registers
`JsonOutboxMessageSerializer` (UTF-8 JSON) as the fallback, so this section is optional. A serializer
set here is registered **for this provider key**: another provider keeps its own one (or the
container-level one), so a container with several providers can hold different codecs side by side.
Configure it at the core level (`AddSaOutbox(b => b.WithMessageSerializer(...))`) when every provider
should share one.

Where inside `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(OrderSerializer.Instance)   // singleton instance
    // .WithMessageSerializer<MySerializer>()                   // DI-resolved singleton
    // .WithMessageSerializer(sp => sp.GetRequiredService<MySerializer>())  // factory
)
```

### AOT-friendly serializer

For Native AOT, avoid reflection-based serialization:

```csharp
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OrderCreated))]
public partial class OrderJsonContext : JsonSerializerContext { }

public class OrderSerializer : IOutboxMessageSerializer
{
    public T? Deserialize<T>(Stream stream) => typeof(T) switch
    {
        Type t when t == typeof(OrderCreated) =>
            (T?)(object?)JsonSerializer.Deserialize(stream, OrderJsonContext.Default.OrderCreated),
        _ => default
    };

    public void Serialize<T>(Stream stream, T value)
    {
        if (typeof(T) == typeof(OrderCreated))
            JsonSerializer.Serialize(stream, value!, OrderJsonContext.Default.OrderCreated);
    }
}
```

## 5. Poison Handler (permanent failures)

`__error$` and the task row already persist every terminal failure, so the provider does not need a
dead-letter queue. If the application needs its own sink — forward the payload to another queue,
object storage, or raise an alert — register `IPgOutboxPoisonHandler`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithPoisonHandler<MyPoisonHandler>()                              // DI-resolved
    // .WithPoisonHandler(new MyPoisonHandler())                       // instance
    // .WithPoisonHandler(sp => new MyPoisonHandler(sp.GetRequiredService<MyStore>()))
)
```

The handler receives `PgOutboxPoisonMessage`: `OutboxId`, `TaskId`, `PayloadId`, `PayloadType`,
`ConsumerGroup`, `TenantId`, `Part`, `Attempt`, `StatusCode`, `StatusMessage`, `Exception`, and the
payload serialized with the core `IOutboxMessageSerializer`.

It is called for every `Error`/`Error5xx` and for `MaximumAttemptsError`, **before** the task is
finalized, and its return value selects the strategy:

| Return | Result |
|---|---|
| `PgOutboxPoisonAction.Default` | keep the built-in persistence: `__error$` row + task finalized with `error_id` |
| `PgOutboxPoisonAction.Handled` | the handler owns the error record: `__error$` is skipped; the task is still finalized as `Error`/`MaximumAttemptsError` with `error_id = NULL` |
| throw | the batch is not finalized, the task stays `Processing`, its lock expires and it is re-rented, so the handler runs again (at-least-once) |

The default `NoOpPgOutboxPoisonHandler` returns `Default`, which keeps the previous behaviour.

### `Handled` with an empty handler

Returning `Handled` and doing nothing is a **drop that leaves only the skeleton behind**:

| Item | Result |
|---|---|
| `__error$` | not written |
| `__log$` (delivery log) | a row is still written: status `Error`/`508`, `status_message`, `payload_id`, group, tenant, `task_id`, `transact_id`, `error_id = NULL` |
| `task$` | terminal `Error`/`Error5xx`/`508`, `error_id = NULL`, `delivery_attempt + 1`, `lock_expires_on` updated |
| `msg$` | the payload row stays unchanged |
| re-delivery | none — `Error` is not in the rent status set |

The payload is therefore not lost physically: it can still be found through `task$`/`__log$` by
`msg_payload_id` and then through `msg$` by `msg_id`, until cleanup drops the partition. What is lost
is the error record: no exception type or stack in `__error$`, and `status_message` holds only the text
the consumer passed to `Error(...)`.

| Handler result | `__error$` | `task$` | Retry | Data |
|---|---|---|---|---|
| `Default` | written (de-duplicated) | `Error`, `error_id` | no | payload in `msg$`, error in `__error$` |
| `Handled` + own store | no | `Error`, `error_id = NULL` | no | payload owned by the handler |
| `Handled` + nothing | no | `Error`, `error_id = NULL` | no | payload only in `msg$`/`__log$` until cleanup |
| throw | no | stays `Processing` | yes, after the lock expires | payload in `msg$` |

Return `Handled` only after the sink succeeded. If the write failed, either `throw` (the task stays
claimable and the handler runs again) or return `Default` (the provider writes `__error$`).

## 6. Table Settings

Where inside `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithOutboxSettings((_, settings) =>
    {
        // ── Schema ─────────────────────────────────────────
        settings.TableSettings.WithSchema("my_outbox");

        // ── Base table name (all tables derive from it) ────
        settings.TableSettings.UseBaseTableName("outbox");
        // Results: outbox, outbox__msg$, outbox__log$, outbox__error$, etc.

        // ── FillFactor per table ───────────────────────────
        settings.TableSettings.Message.FillFactor = 100;       // append-only, never updates
        settings.TableSettings.TaskQueue.FillFactor = 65;      // read-write, needs free space

        // ── Custom field names ─────────────────────────────
        settings.TableSettings.TaskQueue.Fields.TaskId = "id";
        settings.TableSettings.TaskQueue.Fields.TenantId = "client_id";
        settings.TableSettings.TaskQueue.Fields.ConsumerGroup = "grp";

        // ── Individual table names ─────────────────────────
        settings.TableSettings.UseBaseTableName("events");
        // or individually:
        settings.TableSettings.WithMsgTableName("inbox_messages");
        settings.TableSettings.WithDeliveryTableName("inbox_log");

        // ── Migration (partition creation) ─────────────────
        settings.MigrationSettings.AsBackgroundJob = true;     // default
        settings.MigrationSettings.ForwardDays = 2;            // create partitions N days ahead
        settings.MigrationSettings.ExecutionInterval = TimeSpan.FromHours(6);

        // ── Cleanup (old partition removal) ────────────────
        settings.CleanupSettings.AsBackgroundJob = true;       // default
        settings.CleanupSettings.DropPartsAfterRetention = TimeSpan.FromDays(30);
        settings.CleanupSettings.ExecutionInterval = TimeSpan.FromHours(4);

        // ── Min offset (prevent reprocessing) ──────────────
        settings.ConsumeSettings.WithMinOffset("cg_order_consumer", DateTimeOffset.Now);
    })
)
```

### Table overview

| Setting property | Default table name | Role | FillFactor |
|---|---|---|---|
| `Message` | `outbox__msg$` | Source messages (BINARY COPY target) | 100 |
| `TaskQueue` | `outbox` | Active task queue (SKIP LOCKED) | 65 |
| `Delivery` | `outbox__log$` | Delivery history | 100 |
| `Error` | `outbox__error$` | Permanent errors | 100 |
| `Type` | `outbox__type$` | Type → hash cache | 100 |
| `Offset` | `outbox__offset$` | Consumer group offsets | 100 |

### Partitioning strategy

| Table | Partition key | Sort key |
|---|---|---|
| Message | `tenant_id` + `msg_part` | `msg_created_at` |
| TaskQueue | `tenant_id` + `consumer_group` | `task_created_at` |
| Delivery | `tenant_id` + `consumer_group` | `delivery_created_at` |
| Error | date only | `error_created_at` |
| Type / Offset | none | — |

## 7. Running Jobs Manually

By default migration and cleanup run as background jobs via `Sa.Schedule`. You can also trigger them on demand:

```csharp
// The outbox migration service lives in the outbox's own DI scope ("outbox").
var migrationService = host.Services.GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
bool ok = await migrationService.WaitMigrationAsync(TimeSpan.FromSeconds(30), ct);

// Or check current state
if (!migrationService.OnMigrated.IsCancellationRequested)
{
    // No migration has completed yet: delivery jobs are held back until the first one does
}
```
