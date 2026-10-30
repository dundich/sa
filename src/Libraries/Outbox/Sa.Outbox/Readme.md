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
        .WithDataSource(ds => ds.Options(ob => ob.Configure(o => o.ConnectionString = "Host=localhost;Database=outbox")))
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

> **Isolation.** Delivery jobs run in the Outbox's **own** named `Sa.Schedule` schedule under the
> scope key `OutboxScope.DefaultKey` (`"outbox"`), so they never share a schedule with the
> application's `AddSaSchedule()`. The PostgreSQL integration registers its
> `Sa.Partitional.PostgreSql` module under the same key. Both `AddSaOutbox` and
> `AddSaOutboxUsingPostgreSql` accept an optional trailing `scopeKey` argument — pass the same value
> to both to move the Outbox to a different scope. The reserved `"default"` key is rejected.

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
| `MaxBatchSize` | 128 | Max messages per batch |
| `BatchingWindow` | 3 sec | Message aggregation window |
| `PerTenantTimeout` | 0 | Timeout per tenant processing |
| `PerTenantMaxDegreeOfParallelism` | 1 | Tenant parallelism (1 = sequential, -1 = all cores) |
| `RetryCountOnError` | 0 | Retries on error (-1 = infinite) |
| `Paused` | false | Pause flag |
| `IdleMaxInterval` | 0 (disabled) | Upper bound of the adaptive empty-queue poll back-off; must be `>= Interval` when set |

### Empty-Queue Wait Policy

Empty-queue back-off is opt-in per consumer group. Set the base `Interval` (used while work flows) and the cap `IdleMaxInterval` (used while empty):

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        .AddDelivery<OrderConsumer, OrderPlaced>("orders", (_, cs) => cs
            .WithInterval(TimeSpan.FromSeconds(5))      // 5 s while work is flowing
            .WithIdleBackoff(TimeSpan.FromMinutes(2))   // grow up to 2 min while empty
        )
        .AddDelivery<AuditConsumer, AuditEvent>("audit", (_, cs) => cs
            .WithInterval(TimeSpan.FromSeconds(1))
            .WithFixedInterval()                        // keep this group responsive
        )
    ));
```

The DI default is `ExponentialIdleWaitStrategy` (x2 per empty poll, 20% jitter); it is inert until a group calls `WithIdleBackoff`. Override the algorithm globally by type or by instance:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()                                   // custom batch-size calculator
    .WithIdleWaitStrategy(new ExponentialIdleWaitStrategy(1.5, 0.3))   // custom multiplier / jitter
    // or: .WithIdleWaitStrategy<MyIdleStrategy>()                      // fully custom IEmptyQueueWaitStrategy
);
```

See `Delivery/Readme.md` → *Configuring the wait policy* for a custom `IEmptyQueueWaitStrategy` implementation.

---

### Global DI Overrides

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()          // custom batch-size calculator
    .WithIdleWaitStrategy<MyIdleStrategy>()    // custom empty-queue wait strategy
);
```

Both are optional: `IDeliveryBatcher` defaults to `DeliveryBatcher` (passthrough of `MaxBatchSize`), and `IEmptyQueueWaitStrategy` defaults to `ExponentialIdleWaitStrategy`. The default idle strategy is inert until a group opts in with `WithIdleBackoff(maxInterval)`, so out-of-the-box behavior is a fixed `Interval` poll.

---

### Multi-Tenancy

The tenant source is set by choosing **one** of the modes below — they are mutually
exclusive, so pick a single chain per registration.

```csharp
// Mode 1: explicit static list of tenant IDs
.WithTenants((_, ts) => ts.WithTenantIds(1, 2, 3))
```

```csharp
// Mode 2: auto-detect tenants from messages at runtime
.WithTenants((_, ts) => ts
    .WithAutoDetect()                                // auto-detect from messages
    .WithTenantDetector<TenantDetector>()            // custom detector
    .WithTenantParallelProcessing(3)                 // parallel processing per tenant
)
```

> **Note:** `WithTenantIds`, `WithTenantSource`, and `WithTenantProvider` all mark the
> tenant source as authoritative by disabling auto-detection internally
> (equivalent to appending `.WithNoAutoDetect()`). Any of them can be combined with
> `WithTenantParallelProcessing`, but never with the auto-detect chain above. Once any of
> them is called, the plug-service auto-detect branch is skipped and the supplied
> list/source/provider is used verbatim.
>
> The two modes are mutually exclusive and enforced at configuration time: calling
> `WithAutoDetect()` after any of these throws <c>InvalidOperationException</c> instead of
> silently ignoring the explicit source. Choose one mode per registration.

---

### Message Metadata

```csharp
// Option 1: explicit partName and PayloadId resolver
options.AddMetadata<MyMessage>(partName: "orders", getPayloadId: m => m.Id);

// Option 2: derive from IOutboxPublishable
options.AddMetadata<MyMessage>();
```

Both `class` and `struct` payloads are accepted. For a value type, register its metadata
(`AddMetadata<T>()` when it implements `IOutboxPublishable`, or `AddMetadata<T>(partName, getter)`)
so the payload id is resolved through a typed delegate. An unregistered struct that implements
`IOutboxPublishable` still works, but its fallback path boxes the payload.

---

## Available Providers

| Provider | Package | Status |
|----------|---------|--------|
| PostgreSQL | `Sa.Outbox.PostgreSql` | ✅ production-ready |
| Redis | `Sa.Outbox.Redis` | 🔧 in development |

---

## Provider Requirements

A provider must implement three key interfaces:

| Interface | Purpose |
|-----------|---------|
| `IOutboxBulkWriter` | Bulk message insertion into DB |
| `IOutboxDeliveryManager` | Locking and message dispatch |
| `ITenantSource` | Tenant ID source |

> **Consumer ownership.** The `messages` memory passed to `IConsumer.Consume` and every context
> in it are owned by the pipeline for the duration of that call. The contexts are recycled once
> the statuses are persisted, so a consumer must not retain them (or the payload reached through
> them) beyond the returned task. `OutboxMessage<T>`, `OutboxPartInfo`, `OutboxTaskDeliveryInfo`
> and `OutboxDeliveryMessage<T>` are `readonly record struct`s.
>
> **Provider shape.** `IOutboxBulkWriter` has an `OutboxMessageBatch<T>` overload carrying one
> shared part and no per-message wrapper. Its default implementation materializes the old
> `OutboxMessage<T>` shape, so an existing provider keeps working until it overrides the overload.

### Keeping the payload after the call

Take the payload object itself, not the context:

```csharp
public async ValueTask Consume(
    OutboxConsumerSettings settings,
    OutboxMessageFilter filter,
    ReadOnlyMemory<IOutboxContextOperations<OrderCreated>> messages,
    CancellationToken ct)
{
    var owned = new List<OrderCreated>(messages.Length);

    foreach (IOutboxContextOperations<OrderCreated> message in messages.Span)
    {
        // The reference (or the value, for a struct) belongs to you from here on.
        owned.Add(message.Payload);
        message.Ok();
    }

    // The contexts are recycled when Consume returns; `owned` is not.
    await _inbox.SaveAsync(owned, ct);
}
```

If `TMessage` is mutable, or a custom `IOutboxMessageSerializer` can return pooled/reused
buffers, clone the data inside `Consume` instead of keeping the reference:

```csharp
byte[] owned = JsonSerializer.SerializeToUtf8Bytes(message.Payload);
```

Do not keep the `IOutboxContextOperations<T>` instance (or a `messages` slice) in a field,
static collection, background task, or log scope.

---

## Dependencies

- **Sa.Schedule** — background job scheduler
- Reference classes from **Sa** (LockRenewer, MurmurHash3, Retry, extensions)

---

## License

MIT
