# Sa.Outbox.PostgreSql

An AOT-compatible library for implementing the **Transactional Outbox** pattern using PostgreSQL in .NET applications. Provides guaranteed message delivery with support for multi-tenancy, concurrent processing, partitioning, and advanced consumer management.

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
| `WithMaxBatchSize(n)` | 16 | Max messages per batch |
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
┌──────────────┐   BINARY COPY    ┌──────────────┐
│  Your code   │ ───────────────→ │ outbox__msg$ │
│  Publish()   │                  └──────┬───────┘
└──────────────┘                         │ INSERT INTO outbox (SKIP LOCKED)
                                         ↓
                                    ┌──────────────┐
                                    │    outbox     │ ← your IConsumer<T>
                                    │  (task queue) │    reads & processes
                                    └──────┬───────┘
                                           │
                              ┌────────────┼────────────┐
                              ↓            ↓            ↓
                        ┌──────────┐ ┌──────────┐ ┌──────────┐
                        │ __log$   │ │ __error$ │ │ __offset │
                        │ history  │ │ permanent│ │ updated  │
                        └──────────┘ └──────────┘ └──────────┘
```

---

## Result Statuses

After processing each message, call exactly one method:

| Method | When to use | Next action |
|---|---|---|
| `msg.Ok()` | Everything went fine | Task removed |
| `msg.Created()` | Side-effect resource created | Task removed |
| `msg.Accepted()` | Accepted for async processing | Task removed |
| `msg.NoContent()` | Processed, no data to return | Task removed |
| `msg.Error(ex)` | Unrecoverable failure | Logged to `__error$`, no retry |
| `msg.Warn(ex)` | Transient issue (network, timeout) | Requeued, processed next poll |
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
| **UUID v7** | Monotonically increasing IDs from timestamp |
| **BINARY COPY** | Maximum throughput for bulk message insertion |
| **SKIP LOCKED** | Multiple workers safely compete for tasks |
| **Advisory Locks** | Offset coordination per consumer group + tenant |
| **murmurHash3** | Compact type identification cached in `__type$` |
| **SqlCacheSplitter** | Splits large UPDATE queries into chunks up to the shared `DefaultMaxLen` (1024) |

---

## Requirements

- **.NET 10.0** or higher
- **PostgreSQL 15+**
- Compatible with **Native AOT**

## License

MIT
