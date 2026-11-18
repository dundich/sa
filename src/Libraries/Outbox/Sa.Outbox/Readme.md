# Sa.Outbox

**English** | [Русский](Readme-ru.md)

Base infrastructure library for implementing the **Transactional Outbox** pattern in distributed .NET systems. Guarantees atomic message recording alongside business operations within a single database transaction, with reliable delivery, retries, locking, multi-threading, and multi-tenancy support.

Defines abstractions and core logic — concrete database work is implemented by provider packages (`Sa.Outbox.PostgreSql`, `Sa.Outbox.Redis`).

---

## Contents

- [Quick Start](#quick-start)
  - [1. Install a provider package](#1-install-a-provider-package)
  - [2. Configure DI](#2-configure-di)
- [Architecture](#architecture)
  - [Two lifecycle stages](#two-lifecycle-stages)
- [Delivery Status Codes](#delivery-status-codes)
- [Configuration](Configuration/Readme.md)
  - [Consumer Registration](Configuration/Readme.md#consumer-registration)
  - [Runtime Settings Management](Configuration/Readme.md#runtime-settings-management)
  - [Consumption Settings](Configuration/Readme.md#consumption-settings)
  - [Empty-Queue Wait Policy](Configuration/Readme.md#empty-queue-wait-policy)
  - [Global DI Overrides](Configuration/Readme.md#global-di-overrides)
  - [Publish Settings](Configuration/Readme.md#publish-settings)
  - [Serialization](Configuration/Readme.md#serialization)
  - [Multi-Tenancy](Configuration/Readme.md#multi-tenancy)
  - [Message Metadata](Configuration/Readme.md#message-metadata)
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

Consumer registration, consumption and publication settings, serialization, multi-tenancy and
runtime management — in a separate document: [Configuration/Readme.md](Configuration/Readme.md).

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
