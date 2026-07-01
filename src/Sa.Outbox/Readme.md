# Sa.Outbox

Base infrastructure library for implementing the **Transactional Outbox** pattern in distributed .NET systems. Guarantees atomic message recording alongside business operations within a single database transaction, with reliable delivery, retries, locking, multi-threading, and multi-tenancy support.

Defines abstractions and core logic — concrete database work (PostgreSQL, SQL Server, etc.) is implemented by provider packages (`Sa.Outbox.PostgreSql`, `Sa.Outbox.SqlServer`, etc.).

---

## Quick Start

### 1. Install a provider package

```bash
dotnet add package Sa.Outbox.PostgreSql
```

### 2. Configure DI

```csharp
builder.Services
    .AddSaOutbox(builder => builder
        .WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
        .WithDeliveries(b => b.AddDelivery<MyConsumer, MyMessage>())
    )
    // Provider registration (example — PostgreSQL)
    .AddSaOutboxUsingPostgreSql(cfg => cfg
        .WithDataSource(ds => ds.WithConnectionString("Host=localhost;Database=outbox"))
    );
```

---

## Architecture

```
┌──────────────┐     Publish      ┌─────────────┐
│  Application │ ───────────────► │ outbox__msg$│
│              │                  │   (source)   │
│  IConsumer   │                  └──────┬──────┘
│              │                         │ RentDelivery (SKIP LOCKED)
│              │                         ▼
└──────────────┘               ┌─────────────┐
      ▲                        │    outbox   │
      └── Ack/Warn/Error ◄─────┤  (queue)    │
                               └─────────────┘
```

### Two lifecycle stages

| Stage | Description |
|-------|-------------|
| **Publication** | Messages are written to the outbox table inside the business operation's transaction via `IOutboxBulkWriter.InsertBulk()` |
| **Delivery** | Background jobs (`Sa.Schedule`) acquire locked messages, invoke consumers, update status |

---

## Key Types

| Type | Purpose |
|------|---------|
| `IOutboxBuilder` | Fluent configuration builder |
| `IOutboxMessagePublisher` | Publish messages to outbox |
| `IConsumer\<TMessage\>` | Message consumer interface |
| `IOutboxContextOperations\<TMessage\>` | Delivery status change operations (`Ok`, `Error`, `Warn`, `Postpone`, etc.) |
| `OutboxConsumerSettings` | Immutable snapshot of consumer group settings (interval, batches, concurrency, retries…) |
| `OutboxConsumerSettingsBuilder` | Fluent builder for creating and partially updating `OutboxConsumerSettings` |
| `IOutboxConsumerManager` | Runtime manager: atomic swap, pause/resume, change subscriptions |
| `IDeliverySnapshot` | Read-only view of registered deliveries for diagnostics |
| `DeliveryStatus` / `DeliveryStatusCode` | HTTP-like delivery status codes |
| `ExponentialBackoffRetryStrategy` | Exponential backoff with jitter |
| `OutboxPartInfo` | Part info: TenantId, PartName |

---

## Delivery Status Codes

Full set of HTTP-like status codes:

| Code | Status | Meaning |
|------|--------|---------|
| 200 | `Ok()` | Successfully processed |
| 201 | `Created()` | Side-effect resource created |
| 202 | `Accepted()` | Accepted for async processing |
| 203 | `Ok203()` | Non-Authoritative Information |
| 204 | `NoContent()` | Processed, no data |
| 299 | `Aborted()` | Intentionally skipped |
| 301 | `MovedPermanently()` | Moved to another queue |
| 400 | `Warn()` | Transient error → retry |
| 500–507 | `ErrorXXX()` | Permanent error |
| 508 | `ErrorMaxAttempts()` | Max attempts exhausted |
| 103 | `Postpone()` | Deferred processing |
| 104 | `Retry()` | Retry now |

---

## Status Methods

After processing each message, call exactly one method from `IOutboxContextOperations<T>`:

| Method | Description |
|--------|-------------|
| `msg.Ok(message?)` | Successfully processed (200 OK) |
| `msg.Created(message?)` | Side-effect resource created (201 Created) |
| `msg.Accepted(message?)` | Accepted for async processing (202 Accepted) |
| `msg.NoContent(message?)` | Processed, no data (204 No Content) |
| `msg.Aborted(message?)` | Intentionally skipped (299 Aborted) |
| `msg.Warn(exception, message?, postpone?)` | Transient error → retry (400 Warn) |
| `msg.Error(exception, message?)` | Permanent error (500 Error) |
| `msg.ErrorMaxAttempts()` | Max attempts exhausted (508) |
| `msg.Postpone(delay, message?)` | Defer processing (103 Postpone) |
| `msg.Retry(delay, message?)` | Retry with metadata (104 Retry) |

---

## Configuration

### Consumer Registration

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        // Singleton delivery (one instance for the whole app)
        .AddDelivery<MyConsumer, MyMessage>("orders", (sp, cs) => {
            cs
                .WithMaxBatchSize(32)
                .WithLockDuration(TimeSpan.FromSeconds(10))
                .WithMaxDeliveryAttempts(5)
                .WithInterval(TimeSpan.FromSeconds(30))
                .WithInitialDelay(TimeSpan.FromSeconds(5));
        })
        // Scoped delivery (DI-scoped per delivery)
        .AddDeliveryScoped<TenantAwareConsumer, EventData>("events")
    )
);
```

> **Self-bootstrapping:** `DeliveryJob` automatically registers settings into `IOutboxConsumerManager` on first execution. No separate bootstrap service needed — each job reads live snapshots from the manager, including runtime changes via `Apply()`.

### Runtime Settings Management

`IOutboxConsumerManager` allows changing settings without restarting:

```csharp
// Atomic swap — new snapshot applied atomically
manager.Apply("orders", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("orders");
manager.Resume("orders");

// Subscribe to changes
using var sub = manager.Subscribe("orders", updated =>
{
    // react to settings change
});

// Check state
bool paused = manager.IsPaused("orders");
bool registered = manager.IsRegistered("orders");

// Unregister (removes settings AND detaches external control)
manager.Unregister("orders");

// List all registered groups
var allGroups = manager.GetAllConsumerGroupIds();
```

### Consumption Settings

`OutboxConsumerSettings` is a single immutable record. All parameters are set through `OutboxConsumerSettingsBuilder`:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ConsumerGroupId` | — | Unique group identifier |
| `AsSingleton` | true | Singleton (one per cluster) vs Scoped |
| `Interval` | 1 min | Execution period between iterations |
| `InitialDelay` | 10 sec | Delay before first execution |
| `ConcurrencyLimit` | 1 | Number of parallel workers |
| `MaxConcurrency` | 48 | Absolute ceiling of processors |
| `IterationDelay` | 0 sec | Delay between iterations within a cycle |
| `MaxProcessingIterations` | 10 | Iterations per cycle (-1 = unlimited) |
| `LockDuration` | 10 sec | Record lock TTL |
| `LockRenewal` | 3 sec | Lock renewal interval |
| `LookbackInterval` | 7 days | History search window for unprocessed messages |
| `MaxDeliveryAttempts` | 3 | Max delivery attempts before DLQ |
| `MaxBatchSize` | 16 | Max messages per batch |
| `BatchingWindow` | 3 sec | Message aggregation window |
| `PerTenantTimeout` | 0 | Timeout per tenant processing |
| `PerTenantMaxDegreeOfParallelism` | 1 | Tenant parallelism (1 = sequential, -1 = all cores) |
| `RetryCountOnError` | 0 | Retries on error (-1 = infinite) |
| `Paused` | false | Pause flag |

---

### Multi-Tenancy

```csharp
.WithTenants((_, ts) => ts
    .WithTenantIds(1, 2, 3)                          // Explicit list
    .WithAutoDetect()                                // Auto-detect from messages at runtime
    .WithTenantDetector<TenantDetector>()            // Custom detector
    .WithTenantParallelProcessing(3)                 // Parallel processing per tenant
)
```

---

### Message Metadata

```csharp
// Option 1: explicit partName and PayloadId resolver
options.AddMetadata<MyMessage>(partName: "orders", getPayloadId: m => m.Id);

// Option 2: derive from IOutboxPublishable
options.AddMetadata<MyMessage>();
```

---

## Available Providers

| Provider | Package | Status |
|----------|---------|--------|
| PostgreSQL | `Sa.Outbox.PostgreSql` | ✅ production-ready |
| SQL Server | `Sa.Outbox.SqlServer` | 🔧 in development |
| Redis | `Sa.Outbox.Redis` | 🔧 in development |

---

## Provider Requirements

A provider must implement three key interfaces:

| Interface | Purpose |
|-----------|---------|
| `IOutboxBulkWriter` | Bulk message insertion into DB |
| `IOutboxDeliveryManager` | Locking and message dispatch |
| `ITenantSource` | Tenant ID source |

---

## Dependencies

- **Sa.Schedule** — background job scheduler
- Reference classes from **Sa** (LockRenewer, MurmurHash3, Retry, extensions)

---

## License

MIT
