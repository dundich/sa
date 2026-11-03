# Sa.Outbox

**English** | [Русский](Readme-ru.md)

Base infrastructure library for implementing the **Transactional Outbox** pattern in distributed .NET systems. Guarantees atomic message recording alongside business operations within a single database transaction, with reliable delivery, retries, locking, multi-threading, and multi-tenancy support.

Defines abstractions and core logic — concrete database work (PostgreSQL, SQL Server, etc.) is implemented by provider packages (`Sa.Outbox.PostgreSql`, `Sa.Outbox.SqlServer`, etc.).

---

## Contents

- [Quick Start](#quick-start)
  - [1. Install a provider package](#1-install-a-provider-package)
  - [2. Configure DI](#2-configure-di)
- [Architecture](#architecture)
  - [Two lifecycle stages](#two-lifecycle-stages)
- [Delivery Status Codes](#delivery-status-codes)
- [Configuration](#configuration)
  - [Consumer Registration](#consumer-registration)
  - [Runtime Settings Management](#runtime-settings-management)
  - [Consumption Settings](#consumption-settings)
  - [Empty-Queue Wait Policy](#empty-queue-wait-policy)
  - [Global DI Overrides](#global-di-overrides)
  - [Publish Settings](#publish-settings)
  - [Serialization](#serialization)
  - [Multi-Tenancy](#multi-tenancy)
  - [Message Metadata](#message-metadata)
- [Available Providers](#available-providers)
  - [What both providers share](#what-both-providers-share)
  - [Differences at a glance](#differences-at-a-glance)
- [Provider Requirements](#provider-requirements)
  - [Registration and startup validation](#registration-and-startup-validation)
  - [Keeping the payload after the call](#keeping-the-payload-after-the-call)
- [Dependencies](#dependencies)
- [License](#license)

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
> to both to move the Outbox to a different scope. The reserved `"default"` key is rejected. A
> consumer group registered under a scope with no matching provider fails the host start with an
> explicit error instead of silently falling back to the default provider's storage.

Delivery internals — the greedy per-tenant loop, the status acceptance policy, cancellation and the
empty-queue back-off — live in [`Delivery/Readme.md`](Delivery/Readme.md). The provider contract,
registration and startup validation are described in
[`PlugServices/Readme.md`](PlugServices/Readme.md).

---

## Delivery Status Codes

Full set of HTTP-like status codes:

| Code | Status | Meaning |
|------|--------|---------|
| 0 | `Pending` | Stored, not yet delivered (set by the provider) |
| 100 | `Processing` | Rented by a worker (internal state, never set by a consumer) |
| 103 | `Postpone(TimeSpan)` | Deferred processing |
| 104 | `Retry(TimeSpan)` | Retry after the given delay, incrementing the attempt counter |
| 200 | `Ok()` | Successfully processed |
| 201 | `Created()` | Side-effect resource created |
| 202 | `Accepted()` | Accepted for async processing |
| 203 | `Ok203()` | Non-Authoritative Information |
| 204 | `NoContent()` | Processed, no data |
| 299 | `Aborted()` | Intentionally skipped |
| 301 | `MovedPermanently()` | Moved to another queue |
| 400 | `Warn(ex)` | Transient error → retry |
| 500 | `Error(ex)` | Permanent error |
| 501–507 | `Error501(ex)` … `Error507(ex)` | Permanent error |
| 508 | `ErrorMaxAttempts()` | Max attempts exhausted (set by the pipeline, not by a consumer) |

`Pending` and `Processing` are the storage-side states (`DeliveryStatusCode.Pending` / `.Processing`):
the provider writes them on publish and rent, and neither has an operation on
`IOutboxContextOperations<T>`.

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

A group's settings can also come from `IConfiguration` instead of code: `AddDelivery` /
`AddDeliveryScoped` take an optional `configurationKey`, and the core applies the
`Outbox:DeliveryGroups:{group}` section by convention — the prefix is changed (or disabled) with
`WithDeliveryConfigurationSection(...)`:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryConfigurationSection("Outbox:DeliveryGroups") // default; null disables the convention
    .WithDeliveries(d => d.AddDeliveryScoped<OrderConsumer, OrderPlaced>("orders"))
);
```

The convention is implemented by the core, the section itself is bound by the provider package —
see [`Configuration/Readme.md`](../Sa.Outbox.PostgreSql/Configuration/Readme.md#pgoutboxdeliveryoptions)
for the PostgreSQL binding rules (nullable properties, precedence, what is deliberately excluded).

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

Every member also has an `OutboxGroupKey(providerKey, groupId)` overload: with several providers the
same group id may exist under different keys, so the string overloads target the default provider and
throw an ambiguity error when the id is not unique. String keys and their overloads are preserved for
single-provider applications.

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
| `RetryCountOnError` | 0 | Retries of the job (not of the message) on error; `WithInfiniteRetries()` sets `int.MaxValue`, negative values are rejected |
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

See [`Delivery/Readme.md`](Delivery/Readme.md) → *Configuring the wait policy* for a custom
`IEmptyQueueWaitStrategy` implementation.

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

### Publish Settings

`WithPublishSettings` customizes `OutboxPublishSettings` (currently `MaxBatchSize`, default 256) for one outbox
scope:

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithPublishSettings((_, settings) => settings.WithMaxBatchSize(1024)),
    scopeKey: "billing");
```

The record is immutable, so the delegate receives the current instance and returns the next one. That
is the contract: an `Action<OutboxPublishSettings>` could only mutate a copy nobody used, which made
every call a silent no-op. **Migration:** rewrite an `Action`-shaped call as
`(_, settings) => settings.WithMaxBatchSize(n)`; a statement-body lambda must return the settings.

The delegate is keyed by the outbox scope, so each provider keeps its own publish settings; the
default provider also exposes them unkeyed. Several calls for one scope compose in registration
order — each receives the previous result — while a repeated `AddSaOutbox` for the same scope
replaces the callbacks it recorded before instead of applying them twice.

---

### Serialization

`IOutboxMessageSerializer` lives in the core (`Sa.Outbox.Serialization`) and is the **single**
serialization strategy: a provider stores what it writes and reads what it produced, so the
serializer is registered once and applies to publishing and delivery, whichever provider is used.

The core registers `JsonOutboxMessageSerializer` (UTF-8 JSON, reflection-based) as a fallback. A
provider with no serializer of its own uses it; to replace it, register the serializer either at the
core level — one call for every provider — or through a provider's `WithMessageSerializer(...)`:

```csharp
builder.Services
    .AddSaOutbox(builder => builder
        .WithMessageSerializer<MyJsonSerializer>()   // or an instance / a factory
    )
    .AddSaOutboxUsingPostgreSql(cfg => cfg.WithDataSource(...));
```

```csharp
// Same core service, configured on the provider instead:
builder.Services.AddSaOutboxUsingPostgreSql(cfg => cfg
    .WithMessageSerializer(MyJsonSerializer.Instance));
```

`WithMessageSerializer(...)` removes the existing registrations, so the last call wins; a serializer
registered in DI before `AddSaOutbox` also wins over the fallback. The provider-specific
`Sa.Outbox.PostgreSql.Serialization.IOutboxMessageSerializer` /
`Sa.Outbox.Redis.Serialization.IOutboxMessageSerializer` interfaces remain only as compatibility
subtypes of the core one — implement the core interface in new code.

`OutboxPayloadCodec` is the matching byte bridge for providers that store a payload as a
self-contained array (Redis streams): it serializes through a pooled stream and returns an
exact-size array.

For Native AOT, supply a source-generated `JsonSerializerContext`-based serializer (or another
non-reflection format) and deploy the same serializer on the publishing and consuming side before
the first message is written.

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
.WithTenants((_, ts) => ts.WithAutoDetect())
```

The detector itself is a provider concern: the provider package ships one (PostgreSQL reads the
tenant column of the message table) and a custom implementation is plugged at the provider level —
`AddOutboxProvider(..., p => p.WithTenantDetector<MyDetector>())`, see
[`PlugServices/Readme.md`](PlugServices/Readme.md). Per-tenant parallelism is a consumer-group
setting, not a tenant-source setting: `WithPerTenantMaxDegreeOfParallelism(3)` (or
`WithTenantMaxParallelism()` for one worker per CPU) on `OutboxConsumerSettingsBuilder`.

> **Note:** `WithTenantIds`, `WithTenantSource`, and `WithTenantProvider` all mark the
> tenant source as authoritative by disabling auto-detection internally
> (equivalent to appending `.WithNoAutoDetect()`). Any of them can be combined with
> the consumer-level per-tenant parallelism above, but never with the auto-detect chain.
> Once any of them is called, the plug-service auto-detect branch is skipped and the supplied
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
| PostgreSQL | [`Sa.Outbox.PostgreSql`](../Sa.Outbox.PostgreSql) | ✅ production-ready |
| Redis | [`Sa.Outbox.Redis`](../Sa.Outbox.Redis) | 🔧 in development |
| In-memory | built into `Sa.Outbox` | 🧪 reference / dev-test only |

The core is provider-agnostic: the same `AddSaOutbox`, tenant/metadata registrations and consumer
declarations work on top of either package. Only storage and delivery mechanics differ.

### What both providers share

- The same contract: `IOutboxMessagePublisher`, `IConsumer<TMessage>`, `OutboxConsumerSettings`,
  `IOutboxContextOperations<TMessage>` and delivery status codes.
- Ordering per `(tenant, part, payload type)` — never global.
- At-least-once delivery; consumers must be idempotent.
- Tenant isolation and a per-group lookback window.
- Payload identity is the simple type name (`typeof(TMessage).Name`), so two types with the same
  simple name from different namespaces share a channel/stream.
- The core owns batching, the delivery job, `LockRenewer`/`ExtendDelivery` and status interpretation;
  a provider implements `IOutboxBulkWriter`, `IOutboxDeliveryManager` and `IOutboxTenantDetector`,
  and registers them through `AddOutboxProvider` with a descriptor (name + capabilities).
- One serialization strategy: `IOutboxMessageSerializer` and the JSON fallback live in the core, so
  the same serializer is used by every provider (see *Serialization* above).
- Permanent outcomes have a terminal path and a pluggable handler
  (`IPgOutboxPoisonHandler` / `IRedisOutboxPoisonHandler`).

### Differences at a glance

| Aspect | `Sa.Outbox.PostgreSql` | `Sa.Outbox.Redis` |
|---|---|---|
| Storage | tables `outbox__msg$`, `outbox` (task queue), `outbox__log$`, `outbox__error$`, `outbox__type$`, `outbox__offset$` (names derive from the base name, default `outbox`) | streams `stream:{tenant}:{part}:{type}` + PEL + `tenants` set |
| Claiming | `SELECT … FOR UPDATE SKIP LOCKED` on the task queue with `task_lock_expires_on` | `XAUTOCLAIM` by idle time (`PendingClaimIdle`) |
| Retry clock | `task_lock_expires_on` / `BatchingWindow` | PEL idle time only |
| `Postpone`/`Retry` delay | honored (`lock_expires_on = now + delay`) | **ignored**; retried after `PendingClaimIdle` |
| Provider-level attempt limit | consumer-level only (`MaxDeliveryAttempts` in the courier); a task stuck in `Processing` can be re-rented | `XPENDING` delivery count, capped by `RedisOutboxConsumeSettings.MaxDeliveryAttempts` |
| Status persistence | every status is written to the task queue (`outbox`) / `outbox__log$` | only `XACK` / leave pending / poison |
| Poison policy | de-duplicated `__error$` + optional `IPgOutboxPoisonHandler` (`Default` keeps `__error$`, `Handled` skips it) | `IRedisOutboxPoisonHandler` (default drops with `XACK`); no DLQ |
| Retention | partition-based cleanup and migration (`CleanupSettings`) | inline `XTRIM MINID` with a safe floor, off by default |
| Publish transaction | can join the caller's `PgTransaction` | no publish context (`NotSupportedException`) |
| Schema/migration | migrations run as background jobs | none; keys appear on first publish |
| Delivery log | `outbox__log$` | none; the stream entry remains until retention trims it |
| Group start | `__offset$` per `(group, tenant)` | group cursor (`RedisOutboxConsumeSettings.StartFrom` only when created) |
| Cluster / HA | PostgreSQL replication/HA | single-key commands, safe on Redis Cluster |
| Best fit | transactional consistency and audit | high-throughput append-only log, simple operations |

> The providers are intentionally **not** interchangeable at the storage level: switching packages
> does not migrate pending messages. Drain the old provider before switching.

### In-memory provider (`Sa.Outbox.InMemory`)

A reference implementation of the plug point and a development/test substitute: messages live in a
process-local dictionary, so there is no infrastructure to run and no configuration to bind. Register
it with `AddSaOutboxUsingInMemory(...)`.

The provider has its own readme — behaviour, cancellation and retry semantics, and the limitations
that keep it out of production: [`InMemory/Readme.md`](InMemory/Readme.md).

> The in-memory provider is **not** a production provider. It exists so the pipeline can be exercised
> without a database and so a provider author has a few-hundred-line example instead of a
> several-thousand-line one.

---

## Provider Requirements

A provider must implement three key interfaces:

| Interface | Purpose |
|-----------|---------|
| `IOutboxBulkWriter` | Bulk message insertion into DB |
| `IOutboxDeliveryManager` | Locking and message dispatch |
| `IOutboxTenantDetector` | Tenant ID source (optional; `ITenantSource` is the base contract) |

<<<<<<< HEAD
=======
Step-by-step guide with code, using the in-memory provider as the worked example:
**[`PlugServices/ProviderGuide.md`](PlugServices/ProviderGuide.md)** — the four-file shape, the
`RentDelivery`/`ReturnDelivery`/`ExtendDelivery` rules, registration, the contract checklist and how
to test a provider.

>>>>>>> sdd
### Registration and startup validation

A provider package registers the three contracts through one call, which also declares its identity
and capabilities and validates the installation at startup:

```csharp
services.AddOutboxProvider(
    new OutboxProviderDescriptor(
        "PostgreSql",
        OutboxProviderCapabilities.PublishContext
        | OutboxProviderCapabilities.TenantDetection
        | OutboxProviderCapabilities.LockExtension
        | OutboxProviderCapabilities.Postponement),
    provider => provider
        .WithBulkWriter<OutboxBulkWriter>()
        .WithDeliveryManager<OutboxDeliveryManager>()
        .WithTenantDetector<OutboxTenantDetector>());
```

`AddOutboxProvider` rejects a second provider with a different name and a provider without a bulk
writer; constructing `IOutboxProviderRegistry` rejects a missing delivery manager when consumer groups
exist and a missing detector when the `TenantDetection` capability is declared. The provider is logged
once at startup. See [`PlugServices/Readme.md`](PlugServices/Readme.md) for the full contract.

> **Consumer ownership.** The `messages` memory passed to `IConsumer.Consume` and every context
> in it are owned by the pipeline for the duration of that call. The contexts are recycled once
> the statuses are persisted, so a consumer must not retain them (or the payload reached through
> them) beyond the returned task. `OutboxPartInfo`, `OutboxTaskDeliveryInfo`, `OutboxRentToken`
> and `OutboxDeliveryMessage<T>` are `readonly record struct`s.
>
> **Provider shape (0.18).** `IOutboxBulkWriter` has exactly one abstract method — the batch shape,
> with one shared part and no per-message wrapper; the per-message overloads were removed.
> `OutboxDeliveryMessage<T>` is a flat record (`PayloadId`, `Payload`, `PartInfo`, `DeliveryInfo`)
> with an explicit `RentToken` the provider reads back from `IOutboxContext.RentToken`. See
> [`PlugServices/Readme.md`](PlugServices/Readme.md) for the details and the migration.

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
