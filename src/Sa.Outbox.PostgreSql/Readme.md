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
                    s.ScheduleSettings.WithIntervalSeconds(5).WithImmediate();
                    s.ConsumeSettings.WithMaxBatchSize(16);
                })
            )
        )
        // ② Wire up PostgreSQL
        .AddSaOutboxUsingPostgreSql(cfg => cfg
            .WithDataSource(ds => ds
                .WithConnectionString("Host=localhost;Database=outbox_db;Username=postgres;Password=postgres"))
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
        ConsumerGroupSettings settings,
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

---

## Configuration Guide

Everything flows through two registration calls:

| Call | Purpose |
|---|---|
| `AddSaOutbox(...)` | Core: tenants, consumers, schedules |
| `AddSaOutboxUsingPostgreSql(...)` | PostgreSQL: connection, tables, migration, cleanup |

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
            // Schedule — how often to poll
            settings.ScheduleSettings
                .WithInterval(TimeSpan.FromSeconds(5))
                .WithImmediate();                  // start immediately, don't wait first interval

            // Consumption limits
            settings.ConsumeSettings
                .WithMaxBatchSize(16)               // max messages per batch
                .WithMaxDeliveryAttempts(3)         // stop retrying after N attempts
                .WithBatchingWindow(TimeSpan.FromSeconds(2))
                .WithLockDuration(TimeSpan.FromMinutes(10));
        })
    )
    // ...
)
```

#### ConsumeSettings reference

| Method | Default | Description |
|---|---|---|
| `WithInterval(interval)` | 5 s | Polling frequency |
| `WithImmediate()` | — | Don't wait for first interval |
| `WithMaxBatchSize(n)` | 16 | Max messages per batch |
| `WithMaxDeliveryAttempts(n)` | ∞ | Stop retrying after N attempts |
| `WithBatchingWindow(span)` | 2 s | Wait up to this long to fill a batch |
| `WithLockDuration(span)` | 10 m | Task lock TTL before forced expiry |
| `WithSingleIteration()` | — | Process once then stop (testing) |
| `WithNoBatchingWindow()` | — | Take whatever is available now |

#### Dynamic adjustments inside `Consume()`

```csharp
public async ValueTask Consume(ConsumerGroupSettings settings, ...)
{
    // Change behaviour mid-processing
    settings.ConsumeSettings.WithMaxProcessingIterations(100);
}
```

### 3. PostgreSQL Connection

Where inside `AddSaOutboxUsingPostgreSql`:

```csharp
.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithDataSource(ds => ds
        .WithConnectionString("Host=...;Database=...;Username=...;Password=...")
        // .WithMinimumPoolSize(5)
        // .WithMaximumPoolSize(100)
    )
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
var migrationService = host.Services.GetRequiredService<IMigrationService>();
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
| `msg.Error(ex)` | Unrecoverable failure | Logged to `__error$`, no retry |
| `msg.Warn(ex)` | Transient issue (network, timeout) | Requeued, processed next poll |
| `msg.Postpone(ts)` | Need to wait before retry | Requeued after `ts` |
| `msg.Retry(ts, reason)` | Retry with metadata | Requeued with attempt info |
| `msg.Aborted(reason)` | Intentionally skip | Marked skipped, no retry |

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
| **SqlCacheSplitter** | Splits large UPDATE queries into ≤512-element batches |

---

## Requirements

- **.NET 10.0** or higher
- **PostgreSQL 15+**
- Compatible with **Native AOT**

## License

MIT
