# Sa.Outbox.PostgreSql

An AOT-compatible library for implementing the **Transactional Outbox** pattern using PostgreSQL in .NET applications. Provides guaranteed message delivery with support for multi-tenancy, concurrent processing, partitioning, and advanced consumer management.

### What it actually looks like

By its runtime model this is closer to **Redis Stream** and **Kafka** than to the classic "outbox → broker":

| | Redis Stream | Kafka | here |
|---|---|---|---|
| Log | `XADD` into a stream | append-only partition | `__msg$` + `msg_seq` (BIGSERIAL) |
| Groups | `XGROUP` / `XREADGROUP` | consumer group | `consumer_group` in the group settings |
| Cursor | last-delivered-id per group | `__consumer_offsets` | `__offset$.group_offset_seq` per `(group, tenant)` |
| Partitioning | — | partition | `(tenant, part)` → daily leaves |
| Delivery | PEL + `XACK`, claim by timeout | poll + commit offset | `task_lock_expires_on` + `msg.Ok()/Warn()/…` |
| Ordering | within a stream | within a partition | within `(tenant, part)` |
| Retention | `XTRIM` / `MAXLEN` | time or size | `DROP PARTITION` per day |

What it is *not*: the classic "put it in the outbox, a separate relay publishes it to Kafka/RabbitMQ"
pipeline. There is no broker, no CDC/Debezium and no relay process — the PostgreSQL tables themselves
are the log and the queue, and the consumer reads straight from the database. If you need a bus, this
schema can play that role: consumer groups give the same fan-out.

What a real broker has and this does not:

- no wire protocol or client libraries — the "broker" is a PostgreSQL schema;
- no dynamic rebalancing: groups are static (registered in DI), and parallelism inside a group comes
  from `SKIP LOCKED`, not from partition assignment;
- no global ordering: it exists only within `(tenant, part)`;
- no retention by offset: retention drops whole daily partitions;
- as a bus it works only inside one database — it is shared schema, not network transport.

---

## Quick Start (5 minutes)

One complete example — copy, paste, run:

```csharp
using Microsoft.Extensions.Hosting;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql;
using Sa.Outbox.Publication;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        // ① Register outbox core + tenants + consumers
        .AddSaOutbox(builder => builder
            .WithTenants((_, t) => t.WithTenantIds(1, 2, 3))
            .WithDeliveries(b => b
                .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, s) =>
                {
                    s
                        .WithInterval(TimeSpan.FromSeconds(5))
                        .StartImmediately()
                        .WithMaxBatchSize(16)
                        .WithMaxDeliveryAttempts(3)
                        .WithLockDuration(TimeSpan.FromSeconds(10))
                        .WithLookbackInterval(TimeSpan.FromDays(7));
                })
            )
        )
        // ② Wire up PostgreSQL
        .AddSaOutboxUsingPostgreSql(cfg => cfg
            .WithDataSource(ds => ds
                .Options(ob => ob.Configure(o => o.ConnectionString =
                    "Host=localhost;Database=outbox_db;Username=postgres;Password=postgres")))
            .WithOutboxSettings((_, settings) =>
            {
                settings.TableSettings.WithSchema("outbox");
                settings.CleanupSettings.WithDropPartsAfterRetention(TimeSpan.FromDays(30));
            })
            .WithMessageSerializer(OutboxMessageSerializer.Instance)
        )
    )
.Build();

// ③ Publish a message
var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();
await publisher.Publish([
    new OrderCreated("order-42", "Premium Widget"),
    new OrderCreated("order-43", "Standard Gadget")
], tenantId: 1);

// ④ Run (consumers will pick up messages automatically)
await host.RunAsync();


// ─── Your types ────────────────────────────────────────────────

public sealed record OrderCreated(string PayloadId, string ProductName);

public sealed class OrderConsumer : IConsumer<OrderCreated>
{
    public async ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
        CancellationToken ct)
    {
        foreach (var msg in messages.Span)
        {
            Console.WriteLine($"[{filter.ConsumerGroupId}] {msg.Payload.ProductName}");
            msg.Ok();   // ← mark success
        }
    }
}
```

That's it. The library handles:
- Table creation & partitioning
- BINARY COPY bulk insertion
- Concurrent consumption via `SKIP LOCKED`
- Retry / postpone / error workflows
- Automatic cleanup of old partitions
- Self-bootstrapping of consumer settings into `IOutboxConsumerManager`

---

## Publishing inside a Caller Transaction

Normally every `Publish(...)` writes on its own connection and commits immediately (auto-commit). When the outbox write must commit **atomically** with your own database changes (the classic outbox transactionality), publish inside an `NpgsqlTransaction` you control:

```csharp
using Sa.Outbox.PostgreSql;      // PgOutboxPublishExtensions
using Sa.Outbox.Publication;
using Sa.Data.PostgreSql;        // IPgDataSource / PgTransaction

// Open the transaction through the base PostgreSQL data source:
IPgDataSource dataSource = services.GetRequiredService<IPgDataSource>();
await using PgTransaction sqlTx = await dataSource.BeginTransaction(ct);

// Outbox rows go into the caller's transaction:
await publisher.Publish(events, tenantId: 1, sqlTx.Transaction, ct);

// ... your own inserts on sqlTx.Connection / sqlTx.Transaction ...

await sqlTx.CommitAsync(ct);     // outbox rows become visible atomically with your changes
// -- dispose without commit (or RollbackAsync) discards everything, outbox rows included
```

`PgOutboxPublishExtensions` adds `Publish(...)` / `PublishSingle(...)` overloads that accept an `NpgsqlTransaction`. The binary COPY then runs on that transaction's connection, inside that transaction, and **bypasses the automatic retry layer** — retrying over a foreign or aborted transaction is invalid, so failures bubble to the caller.

Passing no context keeps the default auto-commit behavior. There is deliberately **no ambient/implicit scope** — the transaction is always an explicit argument.

Guards:

- The transaction must belong to the **same database** (host / port / database) as the outbox data source — a foreign transaction throws `InvalidOperationException` before anything is written.
- The connection must be open and the transaction must be its own active transaction; otherwise `InvalidOperationException`.

> **Partition bookkeeping** (`EnsureMsgParts`) always runs on the outbox's own connection, outside your transaction, so a rolled-back publish can leave an empty partition (or a type registry row) behind — harmless leftovers, cleaned up by the normal retention jobs.
>
> **Locking note:** while your transaction is open and has written to a partition, partition DDL from another publish on the same data source (e.g., a first message of a new tenant) can block until your transaction finishes. Commit (or roll back) before publishing more outside the transaction.

---

## Configuration Guide

Everything flows through two registration calls:

| Call | Purpose |
|---|---|
| `AddSaOutbox(...)` | Core: tenants, consumers, schedules |
| `AddSaOutboxUsingPostgreSql(...)` | PostgreSQL: connection, tables, migration, cleanup |

> **Isolation.** The Outbox registers its internal `Sa.Schedule` schedule and its
> `Sa.Partitional.PostgreSql` module under the scope key `"outbox"`
> (`Sa.Outbox.OutboxScope.DefaultKey`), and never touches the application's default schedule or
> partitional module. Both entry points take an optional trailing `scopeKey` parameter — pass the
> same value to `AddSaOutbox` and `AddSaOutboxUsingPostgreSql` to move the Outbox to a different
> scope. The reserved `"default"` key is rejected.

### 1. Tenant Configuration

Where inside `AddSaOutbox`:

```csharp
.AddSaOutbox(builder => builder
    .WithTenants((_, ts) => ts
        .WithTenantIds(1, 2, 3)                         // Fixed list
        // .WithAutoDetect()                             // Discover from messages at runtime
        // .WithTenantDetector<MyDetector>()             // Custom IOutboxTenantDetector
        // .WithTenantParallelProcessing(4)              // Max parallel workers per tenant
    )
    // ...
)
```

### 2. Consumer Registration

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

### Per-group Settings from Configuration (`configurationKey`)

The explicit-`consumerGroupId` overloads of `AddDelivery` / `AddDeliveryScoped` take an optional `configurationKey` — the path of an `IConfiguration` section holding that group's settings (bound to `PgOutboxDeliveryOptions`; see [Configuration/Readme.md](Configuration/Readme.md#pgoutboxdeliveryoptions)):

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
OutboxDefaults  →  configuration section (only when a key is given)  →  configure callback
```

- **Key given** — the section's non-null values land between the defaults and the `configure` callback; a partially-filled section overrides only what it names.
- **No key** — the group is shaped by `OutboxDefaults` + `configure` only; a matching section (if any exists) is never read.
- An absent or empty section is a no-op.
- `Paused` is runtime state (`IOutboxConsumerManager`), `AsSingleton` is fixed by which method you call, and the group identity is pinned to the explicit `consumerGroupId` argument — none of them comes from the section.

The section just has to be part of the `IConfiguration` registered in DI (e.g. appsettings.json under `Host.CreateDefaultBuilder`); the provider reads it lazily at startup, so editing the section and restarting is the configuration workflow.

### Consumer group settings

`OutboxConsumerSettingsBuilder` — the single fluent builder for all consumer group settings:

| Method | Default | Description |
|---|---|---|
| `WithInterval(span)` | 1 min | Polling interval |
| `WithInitialDelay(span)` | 10 s | Delay before the first run after startup |
| `StartImmediately()` | — | Start without waiting for the first interval |
| `WithConcurrencyLimit(n)` | 1 | Concurrent jobs in the group |
| `WithMaxConcurrency(n)` | 48 | Max simultaneously running jobs within the group (high — for cloud deployments with connection pooling; override for local/small deployments) |
| `WithRetryCountOnError(n)` | 0 | Job retries on error (`-1` = infinite) |
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

### Runtime settings management

Settings are automatically registered in `IOutboxConsumerManager` on first job startup. For runtime changes:

```csharp
var manager = host.Services.GetRequiredService<IOutboxConsumerManager>();

// Atomic swap — the new snapshot is applied on the next iteration
manager.Apply("cg_order_consumer", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("cg_order_consumer");
manager.Resume("cg_order_consumer");

// Subscribe to changes
using var sub = manager.Subscribe("cg_order_consumer", updated =>
{
    // react to settings changes
});

// Check state
bool paused = manager.IsPaused("cg_order_consumer");
```

### 3. PostgreSQL Connection

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

### 4. Message Serialization

Where inside `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(OutboxMessageSerializer.Instance)   // singleton instance
    // .WithMessageSerializer<MySerializer>()                   // DI-resolved transient
    // .WithMessageSerializer(sp => sp.GetRequiredService<MySerializer>())  // factory
)
```

#### AOT-friendly serializer

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

### 5. Table Settings

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
        settings.TableSettings.TaskQueue.Fields =
        {
            TaskId = "id",
            TenantId = "client_id",
            ConsumerGroup = "grp"
        };

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
        settings.ConsumeSettings.WithMinOffset<OrderConsumer>(DateTimeOffset.Now);
    })
)
```

#### Table overview

| Setting property | Default table name | Role | FillFactor |
|---|---|---|---|
| `Message` | `outbox__msg$` | Source messages (BINARY COPY target) | 100 |
| `TaskQueue` | `outbox` | Active task queue (SKIP LOCKED) | 65 |
| `Delivery` | `outbox__log$` | Delivery history | 100 |
| `Error` | `outbox__error$` | Permanent errors | 100 |
| `Type` | `outbox__type$` | Type → hash cache | 100 |
| `Offset` | `outbox__offset$` | Consumer group offsets | 100 |

#### Partitioning strategy

| Table | Partition key | Sort key |
|---|---|---|
| Message | `tenant_id` + `msg_part` | `msg_created_at` |
| TaskQueue | `tenant_id` + `consumer_group` | `task_created_at` |
| Delivery | `tenant_id` + `consumer_group` | `delivery_created_at` |
| Error | date only | `error_created_at` |
| Type / Offset | none | — |

### 6. Running Jobs Manually

By default migration and cleanup run as background jobs via `Sa.Schedule`. You can also trigger them on demand:

```csharp
// The outbox migration service lives in the outbox's own DI scope ("outbox").
var migrationService = host.Services.GetRequiredKeyedService<IMigrationService>(OutboxScope.DefaultKey);
bool ok = await migrationService.WaitMigration(TimeSpan.FromSeconds(30), ct);

// Or check current state
if (!migrationService.OnMigrated.IsCancellationRequested)
{
    // DeliveryJob is blocked during active migration
}
```

---

## Message Lifecycle

```
Publish(messages)
      │  COPY (BINARY) — one transaction + advisory lock (tenant, part)
      ▼
  outbox__msg$          ← the message lives here, msg_seq = nextval()
      │
      │ ① materialize — once per consumer group
      │    INSERT INTO outbox … SELECT … WHERE msg_seq > @offset
      │    under the (consumer_group, tenant) advisory lock;
      │    __offset$ is advanced in the same transaction
      ▼
    outbox  (task queue)   ← one row per "message × group" pair
      │
      │ ② rent — SELECT … FOR UPDATE SKIP LOCKED
      │    → delivery_status_code = Processing, task_lock_expires_on
      ▼
  IConsumer<T>.Consume
      │
      │ ③ finish — ReturnDelivery
      │    __log$: one record per delivery attempt
      │    __error$: terminal failures only
      │    outbox: result status + attempt counter
      ▼
  the row stays in outbox with its status and is not rented again
  (Pending/Processing/Postpone/Retry/Warn stay rentable);
  it is physically removed only with its daily partition, on retention
```

Note: `__offset$` is a *materialisation* watermark, not a *delivery* watermark. It moves when the
tasks are created, before the consumer has seen them. That is safe: once it has moved, the tasks are
already in `outbox`, so a crash between materialisation and delivery loses nothing — the tasks are
simply rented later.

### Consumer groups

Every group is an independent copy of the stream. A message in `__msg$` is materialised **once per
group** that subscribes to its type (part + payload type), so groups neither block nor consume each
other's messages:

```
                         outbox__msg$   (shared: LIST(tenant) → LIST(part) → RANGE(day))
                               │
             ┌─────────────────┼─────────────────┐   one copy per group
             ▼                 ▼                 ▼
      ┌─────────────┐   ┌─────────────┐   ┌─────────────┐
      │  "orders"   │   │   "audit"   │   │  "billing"  │
      │ cursor      │   │ cursor      │   │ cursor      │
      │ (grp,tenant)│   │ (grp,tenant)│   │ (grp,tenant)│
      └──────┬──────┘   └──────┬──────┘   └──────┬──────┘
             ▼                 ▼                 ▼
       task queue        task queue        task queue        ← per-group partition
             │                 │                 │
      IConsumer<Order>  IConsumer<Order>  IConsumer<Invoice>  ← per-group handler
             │                 │                 │
             ▼                 ▼                 ▼
      __log$ (grp)      __log$ (grp)      __log$ (grp)        ← per-group delivery log
```

`"orders"` and `"audit"` above are two groups on the **same** message type: both receive every
message, each into its own queue. `"billing"` subscribes to a different type and only gets its own.

What is per group and what is shared:

| Resource | Where | Granularity |
|---|---|---|
| Cursor | `__offset$` | `(consumer_group, tenant)` |
| Task queue | `outbox` | `LIST(tenant) → LIST(consumer_group) → RANGE(day)` |
| Delivery log | `__log$` | `LIST(tenant) → LIST(consumer_group) → RANGE(day)` |
| Schedule and settings (`Interval`, `MaxBatchSize`, `MaxProcessingIterations`, `LockDuration`, …) | the `DeliveryJob` in the scheduler | per group |
| Materialisation lock | advisory lock | `(consumer_group, tenant)` — groups never block each other |
| Messages | `__msg$` | shared |
| Type registry | `__type$` | shared |
| Terminal errors | `__error$` | shared, not split by group (partitioned by day) |

Practical consequences:

- **consumption capacity is per group**, not per database: `MaxBatchSize × MaxProcessingIterations`
  per `Interval` (with the defaults 128 × 10 per minute ≈ 1 280 messages/min per group);
- to consume faster, add groups (different types/parts) or raise the batch and poll frequency — but not
  the number of workers on one group, which is bounded by that group's own lock;
- each group has its own cursor, so a lagging group does not slow the others down;
- a message whose type no group subscribes to stays in `__msg$` and leaves only with its partition on
  retention.

---

## Result Statuses

After processing each message, call exactly one method:

| Method | When to use | Next action |
|---|---|---|
| `msg.Ok()` | Everything went fine | Status written, row is not rented again (removed with its partition) |
| `msg.Created()` | Side-effect resource created | Status written, row is not rented again |
| `msg.Accepted()` | Accepted for async processing | Status written, row is not rented again |
| `msg.NoContent()` | Processed, no data to return | Status written, row is not rented again |
| `msg.Error(ex)` | Unrecoverable failure | Logged to `__error$`, no retry |
| `msg.Warn(ex)` | Transient issue (network, timeout) | Rented again after the batching window |
| `msg.Postpone(ts)` | Need to wait before retry | Requeued after `ts` |
| `msg.Retry(ts, reason)` | Retry with metadata | Requeued with attempt info |
| `msg.Aborted(reason)` | Intentionally skip | Marked skipped, no retry |
| `msg.ErrorMaxAttempts()` | Max attempts exhausted | Logged to `__error$`, no retry |

---

## Database Architecture

```
📂 my_outbox (schema)
├── 📄 outbox__msg$      — Source messages (read-only, binary COPY)
├── 📄 outbox            — Task queue (read-write, SKIP LOCKED)
├── 📄 outbox__log$      — Delivery history (read-only)
├── 📄 outbox__error$    — Permanent errors (read-only)
├── 📄 outbox__type$     — Type ↔ hash registry (read-only)
└── 📄 outbox__offset$   — Consumer group offsets (advisory lock)
```

### Under the hood

| Mechanism | What it solves |
|---|---|
| **`msg_seq` (BIGSERIAL)** | Database-assigned insertion order, and **the** consumption cursor (`__offset$.group_offset_seq`). Denormalised into the task table so `RETURNING msg_seq` advances the cursor in the same statement |
| **UUID v7 message id** | Application-minted id, used as the join key between `__msg$` and the task queue. Deliberately **not** used for ordering: it comes from application clocks, which skew and get backdated |
| **BINARY COPY** | Maximum throughput for bulk message insertion |
| **SKIP LOCKED** | Multiple workers safely compete for tasks — and only for tasks. On the single cursor row "skip" would mean reporting an empty queue while a backlog exists |
| **Advisory Locks** | Offset coordination per `(consumer group, tenant)` |
| **Publish lock** | `pg_advisory_xact_lock` per `(tenant, part)` around the COPY — makes `msg_seq` order equal commit order, so the `max(msg_seq)` cursor cannot pass a row that is still committing |
| **Cursor index** | `(msg_seq)` on `__msg$` — turns the loader's cursor read into an index range scan instead of a Seq Scan of the whole lookback window (measured 56.8 ms → 0.166 ms on 7 × 40k rows) |
| **Daily partitions** | Pruning keeps one poll on a few leaves, and retention is a `DROP PARTITION` (indexes leave with the leaf) instead of a `DELETE` |
| **murmurHash3** | Compact type identification cached in `__type$` |
| **SqlCacheSplitter** | Splits large multi-row INSERT/UPDATE statements into chunks up to the shared `DefaultMaxLen` (1024) |

---

## Bulk Publishing at Scale

### Rules

1. **Batch 256–1024, not less.** Throughput grows with the batch (4.7k → 58k rows/s without the
   lock), because every publish pays a fixed price: BEGIN, lock, COPY, fsync on COMMIT. One publish =
   one `COPY` = one transaction.
2. **One producer per part.** The lock serializes not only the COPY but the commits of the part, so
   group commit stops amortizing fsync: 8 producers into one part give 14.8k rows/s against 55.3k
   without the lock (−67 %). With one producer per part the cost is zero; 8 parts give 43.7k rows/s
   and p50 32 ms against 23.5k/77 ms over three.
3. **Do not mix parts/tenants in one `Publish` call** — otherwise a single `COPY` takes N locks and
   holds all of them until commit.
4. **Keep transactions short.** Publishing inside a business transaction holds the part lock for its
   whole duration. Atomicity requires the same transaction context — which means the business
   transaction must be short, not "5 seconds with an external HTTP call inside".
5. **Do not add indexes to `__msg$`.** Each one costs ~140–200 B of WAL per row and 30–50 % of
   throughput. Exactly one is needed — the cursor index `(msg_seq)`. The PK is the most expensive;
   drop it only deliberately (you lose the duplicate-`msg_id` protection).
6. **Do not `COPY` directly into a leaf** — measured: not faster (72–78k against 87k), routing is not
   the bottleneck.
7. **Leave `msg_seq` alone.** The sequence costs ~4 B of WAL per row and contributes nothing to the
   ceiling. `CACHE` breaks the cursor (message loss).
8. **Server:** `wal_compression=lz4` (or `zstd`) — WAL is the biggest item, the `UNLOGGED` control
   showed up to 2.9× of headroom; `max_wal_size`/`checkpoint_timeout` so checkpoints do not run
   constantly; keep `full_page_writes` on; keep `synchronous_commit` on.
9. **Connection pooling:** `pool_mode=transaction` (not `statement` — the library works in
   transactions). The locks are transaction-scoped, so PgBouncer does not break them.
10. **Partitions:** many daily partitions do not slow inserts down, and retention via `DROP PARTITION`
    is free (indexes go away with the leaf).

### Measurements

Stand: PostgreSQL 15 in Docker, `__msg$` = `LIST(tenant) → LIST(part) → RANGE(day)`, 256 leaves,
PK `(msg_id, tenant_id, msg_part, msg_created_at)` plus the cursor index `(msg_seq)`, 200 B payload,
`synchronous_commit=on` (the real mode), fsync ≈ 11 ms. Client — Npgsql 10, `NpgsqlBinaryImporter`,
exactly as in `BulkInsertMsgCommand`. 8 producers, 8-second runs, median of 3.

The absolute numbers are a property of that stand. Carry them over with the formula at the end of the
section rather than directly; run-to-run spread on this stand is ±30 % (latencies are steadier than
throughput).

**Batch and the cost of the lock** — 8 producers, one part:

| batch | no lock, rows/s | with lock, rows/s | lock cost |
|---|---|---|---|
| 16 | 4 685 | 1 198 | −74 % |
| 64 | 17 578 | 3 524 | −80 % |
| 256 | 44 946 | 14 808 | −67 % |
| 1024 | 58 450 | 26 222 | −55 % |

**Scenarios** — batch 256, lock enabled (the "1 part" configuration is the 256 row above, measured in
a different run):

| parts | rows/s | tx/s | p50, ms | p99, ms |
|---|---|---|---|---|
| 1 | 17 183 | 67 | 99.1 | 413.4 |
| 3 | 23 498 | 92 | 77.5 | 156.3 |
| 8 | 43 683 | 171 | 32.2 | 176.0 |

**WAL per row** — batch 1024, 8 producers:

| table | WAL/row |
|---|---|
| PK + cursor index | 526 B |
| cursor index only | 386 B |
| no indexes | 319 B |
| `UNLOGGED` (control) | 3 B |

**Column legend:**

- `batch` — rows in one `COPY`, i.e. in one publish and one transaction
  (`OutboxPublishSettings.MaxBatchSize`).
- `rows/s` — rows per second, summed over all producers.
- `tx/s` — transactions (publishes) per second; with a known `batch` that is `rows/s / batch`.
- `no lock` / `with lock` — before and after the `(tenant, part)` advisory lock was introduced;
  `lock cost` is the throughput lost to it.
- `parts` — how many distinct `(tenant, part)` are in play; 8 producers over 3 parts means ~2.7
  producers per key.
- `p50` / `p99` — percentiles of one publish's latency on the client, including lock wait.
- `WAL/row` — WAL bytes per inserted row.

**Formula for carrying this over to your hardware:**

```
t_txn ≈ F + batch × t_row
rows/s per part ≈ batch / t_txn
rows/s total    ≈ min(N_parts × rows/s per part, server ceiling)
```

`F` is the effective cost of one commit in your installation (group commit included), `t_row` ≈
0.02 ms per row. Both follow from two points of the batch table: `1/R(B) = F/B + t_row`, so with
`B` = 256 and `B` = 1024 (`R` = 44 946 and 58 450) we get `F ≈ 1.8 ms` and `t_row ≈ 15 µs` — effective
values for 8 producers, not the raw disk fsync.

### What to monitor in production

- `xact_commit/s` and `rows/s` on `__msg$` — the actual cost of publishing;
- WAL bytes/row (`pg_current_wal_lsn` before/after a window) — expect ~350–550 B; growth means extra
  indexes or bloat;
- `pg_stat_activity.wait_event` — `Lock:advisory` for more than a few ms means the key is overloaded
  (too many producers per part);
- delivery lag: `max(msg_seq) − group_offset_seq` per group;
- publish p99 on the client, not only server-side time.

---

## Requirements

- **.NET 10.0** or higher
- **PostgreSQL 15+**
- Compatible with **Native AOT**

## License

MIT
