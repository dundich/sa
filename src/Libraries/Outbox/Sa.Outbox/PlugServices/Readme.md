# PlugServices: the provider plug point

## Why

`Sa.Outbox` does not know where messages live. The whole storage is described by three contracts:

| Contract | Core consumer | When |
|---|---|---|
| `IOutboxBulkWriter` | `OutboxMessagePublisher` | on every publish |
| `IOutboxDeliveryManager` | `DeliveryTenant` | on every rent/return/extend |
| `IOutboxTenantDetector` | `TenantProvider` (optional) | when tenants are auto-detected |

Before this spec each provider package registered them by hand (`RemoveAll<T>().AddSingleton<T, Impl>()` in its own `Setup`), and the commented-out `IOutboxPlugin` gave nothing: the container could not report which provider was installed, a partial registration failed on the first publish/tick instead of at startup, and a second provider package silently overwrote the first.

The goal is a **thin metadata/registration layer**, with no change to the hot path or the public contracts.

## Scope

In:

- provider identity (name, version) and capability declaration;
- one-call registration of the three services;
- fail-fast: a duplicate provider and an incomplete registration are caught at startup;
- a diagnostics registry (`IOutboxProviderRegistry`).

Out (separate stages):

- health checks and `InitializeAsync` (option B);
- several providers in one container via keyed registrations (option C);
- changing the contracts (`InsertBulk`, `Memory<IOutboxContextOperations<T>>`, `IOutboxContextFactory`) — a separate major only.

## Public API

### Capabilities

```csharp
[Flags]
public enum OutboxProviderCapabilities
{
    None = 0,
    PublishContext  = 1 << 0,  // InsertBulk(..., context) really joins the caller's transaction
    TenantDetection = 1 << 1,  // IOutboxTenantDetector is present, CanDetect may be true
    LockExtension   = 1 << 2,  // ExtendDelivery is meaningful (lease renewal)
    Postponement    = 1 << 3,  // Postpone/Retry delays are honoured by the provider
}
```

The flags are declarative: diagnostics plus one validation rule (the `TenantDetection` capability requires a registered detector). They make known divergences explicit — Redis supports no publish context and ignores `Postpone/Retry` delays.

### Descriptor

```csharp
public sealed record OutboxProviderDescriptor(
    string Name,
    OutboxProviderCapabilities Capabilities = OutboxProviderCapabilities.None,
    string? Version = null)
{
    public bool Supports(OutboxProviderCapabilities capability);
}
```

`Version` is for logs and diagnostics; when omitted it is taken from the bulk writer's assembly.

### Registration

```csharp
public static IServiceCollection AddOutboxProvider(
    this IServiceCollection services,
    OutboxProviderDescriptor descriptor,
    Action<OutboxProviderBuilder> configure);

public sealed class OutboxProviderBuilder
{
    public OutboxProviderBuilder WithBulkWriter<T>()      where T : class, IOutboxBulkWriter;
    public OutboxProviderBuilder WithDeliveryManager<T>() where T : class, IOutboxDeliveryManager;
    public OutboxProviderBuilder WithTenantDetector<T>()  where T : class, IOutboxTenantDetector;
}
```

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

Provider packages call it **before** `AddSaOutbox`, so the validator starts before the schedules. `WithDeliveryManager` is optional: a publish-only provider omits it, which is valid while no consumer groups exist.

### Registry

```csharp
public interface IOutboxProviderRegistry
{
    OutboxProviderDescriptor Descriptor { get; }
}
```

A singleton registered by `AddOutboxProvider`, usable by the application's health/diagnostics.

## Validation rules

| Check | When | Result |
|---|---|---|
| `Name` is not blank | `AddOutboxProvider` | `ArgumentException` |
| A bulk writer is registered (`WithBulkWriter`) | `AddOutboxProvider` | `InvalidOperationException` |
| A second provider with a **different** `Name` | `AddOutboxProvider` | `InvalidOperationException` |
| `AddOutboxProvider` repeated with the same `Name` | `AddOutboxProvider` | idempotent: the marker is replaced |
| A provider is registered at all | registry construction | `InvalidOperationException` |
| `IOutboxBulkWriter` is **registered** | registry construction | `InvalidOperationException` |
| `IOutboxDeliveryManager` is **registered** when groups exist | registry construction | `InvalidOperationException` |
| `TenantDetection` capability ⇒ detector is **registered** | registry construction | `InvalidOperationException` |
| Core and provider `scopeKey` agree | — | not checked in this version (see below) |

The registry checks look at **registrations** and never construct the provider's services
(`IServiceProviderIsService`): building a storage chain can open connections and load state, and the
registry is also read while delivery job settings are being built.

Trigger points:

1. `AddOutboxProvider` — static checks (name, writer, duplicate).
2. `OutboxProviderValidationHostedService` — resolves the registry at host start and logs the provider once.
3. The delivery job settings build — when the registry is registered it is resolved and validated before the first tick, covering the case where the schedule was registered before the validator.

## Execution flow

1. **Registration.** `AddSaOutboxUsingPostgreSql(...)` → provider services → `AddOutboxProvider(...)` → `AddSaOutbox(...)`. Order relative to the core does not matter: the registry is built lazily from `IEnumerable<OutboxProviderRegistration>`; a duplicate `Name` fails immediately.
2. **Startup.** The hosted validator resolves the registry: one provider, writer, delivery manager when groups exist, detector when the capability is declared. Failure aborts the start with the provider name in the message.
3. **Publish.** `IOutboxMessagePublisher` → `IOutboxBulkWriter`; `context` is passed through as before (the `PublishContext` flag only documents whether the provider honours it).
4. **Delivery.** `Sa.Schedule` tick → `DeliveryJob` → `DeliveryProcessor` → `TenantProvider` → `DeliveryTenant` → `IOutboxDeliveryManager` → `DeliveryCourier` → `ReturnDelivery`.
5. **Tenants.** `AutoDetect` → `IOutboxTenantDetector`; otherwise `TenantSettings`.
6. **Shutdown.** The descriptor owns no resources; disposal stays with DI and the singletons.

## Compatibility

- Public contracts are unchanged; existing applications behave exactly as before.
- One provider per container (as before), but now it is enforced instead of "last one wins".
- `Sa.Outbox.PlugServices.IOutboxPlugin` (commented out) is **removed**: the descriptor and the registry replace its role, while the services stay in DI.
- Keyed multi-provider support and health are follow-up stages; the descriptor is shaped so `Name` can become a `ServiceKey` without breaking the API.

## Follow-ups

- **B (health/lifecycle):** `IOutboxProviderHealth` with a report (healthy/reason/latency), an adapter for `Microsoft.Extensions.Diagnostics.HealthChecks`, and an optional `InitializeAsync` as a provider-owned hosted service.
- **C (keyed):** `scopeKey` in the descriptor, keyed storage services, `(key, group)` consumer keys.
- **Contracts:** drop the legacy `InsertBulk` overload, add an opaque rent token to `IOutboxContextFactory.Create`, type the publish context.

## Tests

- registration: writer/descriptor/version, missing writer, two different providers, repeated registration;
- registry: no provider, missing delivery manager with groups, publish-only without groups, `TenantDetection` without a detector;
- hosted validator: registered and starts;
- PG/Redis: descriptor names and capability flags;
- the existing PG/Redis/core suites stay green.

---

# Contract simplification (major 0.18)

## Why

The current contracts carry two API generations at once:

- `IOutboxBulkWriter` has four overloads, and **only the legacy one is abstract** — the "simple"
  implementation silently goes through an allocating adapter while the fast path is optional.
  `OutboxMessage<T>` (payload + part wrapper) exists both in publishing and inside the delivery record.
- A rent has no slot for the provider's own identity: Redis packs the stream id into the `Guid
  OutboxId`, PostgreSQL reads `TaskId`/`DeliveryId` from `DeliveryInfo` — the contract never states
  what a provider needs to return a message to the right storage row.

This major removes both layers.

## Breaking changes

| Before | After |
|---|---|
| `InsertBulk(batch, ct)` + `InsertBulk(batch, context, ct)` + two legacy `InsertBulk(ReadOnlyMemory<OutboxMessage<T>>, …)` overloads | one abstract `InsertBulk(batch, context, ct)` + a context-less convenience overload |
| `OutboxMessage<T>(PayloadId, Payload, PartInfo)` | removed; publishing uses `OutboxMessageBatchItem<T>` |
| `OutboxDeliveryMessage<T>(OutboxId, OutboxMessage<T> Message, DeliveryInfo)` | `OutboxDeliveryMessage<T>(OutboxId, PayloadId, Payload, PartInfo, DeliveryInfo, RentToken)` |
| rent identity implicit in `OutboxId` / `DeliveryInfo` | explicit `OutboxRentToken(long Key, long SubKey)`, exposed to the provider as `IOutboxContext.RentToken` |
| "context not supported" thrown by the provider at runtime | `OutboxMessagePublisher` checks the `PublishContext` capability before calling the writer |

## Publishing

```csharp
public interface IOutboxBulkWriter
{
    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default);

    ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        CancellationToken cancellationToken = default)
            => InsertBulk(batch, context: null, cancellationToken);
}
```

- One abstract method: a provider must implement the fast batch path.
- `context` stays `object?` (a typed context is a separate topic); with `context != null` and no
  `PublishContext` capability the publisher throws `NotSupportedException` naming the provider
  **before** any write.

## Delivery and the rent token

```csharp
public readonly record struct OutboxRentToken(long Key, long SubKey = 0)
{
    public static OutboxRentToken None => default;
    public bool IsNone => Key == 0 && SubKey == 0;
}

public readonly record struct OutboxDeliveryMessage<TMessage>(
    Guid OutboxId,
    string PayloadId,
    TMessage Payload,
    OutboxPartInfo PartInfo,
    OutboxTaskDeliveryInfo DeliveryInfo,
    OutboxRentToken RentToken = default);
```

- The provider fills the token on rent and reads it back from `IOutboxContext.RentToken` in
  `ReturnDelivery`/`ExtendDelivery` — no parsing of `OutboxId`, no side tables.
- Two 64-bit halves are exactly what addresses a row: PostgreSQL `(task_id, delivery_id)`, Redis
  `(stream ms, seq)`. Allocation-free.
- `IOutboxContextFactory.Create(delivery)` is unchanged: the token travels inside the delivery record.
- `OutboxId` remains the provider's public message identity (in Redis, the packed stream id as before).

## Provider migration

**PostgreSQL**

- `IBulkInsertMsgCommand`/`BulkInsertMsgCommand`: batch overloads only (tx / no-tx); the legacy
  `Execute(ReadOnlyMemory<OutboxMessage<T>>, …)`, `DistinctPartKeys` and legacy `WriteRows` are
  removed — a batch is always one part, so one advisory key is locked.
- `StartDeliveryCommand`: flat record, `RentToken = (TaskId, DeliveryId)`.

**Redis**

- `OutboxBulkWriter`: two overloads; the legacy part-grouping branch is removed.
- `RentDeliveryCommand`: flat record, `RentToken = (ms, seq)` from the stream id.
- `FinishDeliveryCommand`: the stream id is rebuilt from `context.RentToken` instead of `OutboxId`;
  `RedisStreamId.ToStreamId(Guid)` becomes unused.

## Versioning

- `Sa.Outbox`, `Sa.Outbox.PostgreSql` → **0.18.0**; `Sa.Outbox.Redis` → **0.2.0** (the provider is still in development).
- Third-party provider checklist: implement `InsertBulk(batch, context, ct)`; move to the flat
  `OutboxDeliveryMessage<T>`; fill `RentToken` on rent and use it on return.

## Tests

- core: a single abstract writer method; the publisher throws `NotSupportedException` for a context
  without the capability; `RentToken` reaches the context and comes back to the provider;
- PG: batch insert (one part), `(task, delivery)` token on return;
- Redis: `(ms, seq)` token, `XACK` by token, poison path unchanged;
- the existing PG/Redis integration suites stay green.

---

# Keyed multi-provider (C)

## Goal

Several outbox providers in one container: for example `billing` on PostgreSQL and `audit` on a
different PostgreSQL schema (Redis follows in slice 2). Each provider has **its own** storage,
schedule, migrations/cleanup, tenant set and consumer groups.

## Decisions

1. **Provider key = `scopeKey`.** One provider = one `Sa.Schedule` = one `Sa.Partitional.PostgreSql`
   module. The classic `AddSaOutboxUsingPostgreSql(...)` uses `OutboxScope.DefaultKey` (`"outbox"`)
   and is unchanged.
2. **Metadata and serializer stay shared**: they describe the message format, not the storage.
3. **Tenants are per provider**: `WithTenants` inside the provider's `configureOutbox` registers
   `TenantSettings` under the provider key.
4. **One message type may be published to several providers** — two independent at-least-once flows
   sharing the metadata.
5. **A group is bound to a provider through its `configureOutbox`**, so it cannot be attached to a
   foreign storage by accident.
6. **Configuration sections are `Outbox:{providerKey}:…`**; default-key paths keep their previous
   shape (`Outbox:DeliveryGroups:{group}`).
7. **Consumer group ids are unique in the container** (validated). That is the slice-1 contract: it
   avoids keyed `IConsumer<T>` and composite keys in the manager. Composite keys are slice 3.

## API

```csharp
services
    .AddSaOutboxUsingPostgreSql(
        "billing",                                   // provider key == scope key
        pg => pg.WithDataSource(ds => ds.Options(o => o.Configure(x => x.ConnectionString = billingCs))),
        outbox => outbox
            .WithDeliveries(d => d.AddDelivery<InvoiceConsumer, Invoice>("invoices")))
    .AddSaOutboxUsingPostgreSql(
        "audit",
        pg => pg.WithDataSource(ds => ds.Options(o => o.Configure(x => x.ConnectionString = auditCs))),
        outbox => outbox
            .WithDeliveries(d => d.AddDelivery<AuditConsumer, AuditEvent>("audit-events")));
```

Publishing:

```csharp
IOutboxMessagePublisher billing = sp.GetRequiredKeyedService<IOutboxMessagePublisher>("billing");

sp.GetRequiredService<IOutboxPublisherResolver>().For("billing").PublishSingle(invoice, tenantId: 1);
```

## What is keyed

| Layer | Registration |
|---|---|
| `IOutboxBulkWriter`, `IOutboxDeliveryManager`, `IOutboxTenantDetector` | keyed by provider key |
| `IOutboxMessagePublisher`, `OutboxPublishSettings` | keyed (plus an unkeyed alias for the default key) |
| `IOutboxProviderRegistry` | keyed per provider + aggregate `IOutboxProviders` |
| `IDeliveryGroupOptionsProvider`, `PgPollingSettings`, `PgOutboxSettings`, `IPgDataSource`, `SqlOutboxBuilder`, commands, loader, type resolver | keyed per provider (PG) |
| `TenantSettings` | keyed per provider |
| `IScheduleSettings` / `IScheduler` / `IPartitionManager` | keyed by `scopeKey` — already |
| `IOutboxMessageMetadataProvider`, `IOutboxMessageSerializer` | shared |
| `IConsumer<T>` | keyed by group id (uniqueness validated) |

The default provider (`"outbox"`) additionally registers an unkeyed `IOutboxMessagePublisher` alias,
so existing code injecting the publisher without a key keeps working.

## Delivery routing

`OutboxConsumerSettings.ProviderKey` and `OutboxMessageFilter.ProviderKey` carry the provider key.
`DeliveryTenant`/`DeliveryProcessor` resolve the keyed `IOutboxDeliveryManager` and `ITenantProvider`
through an internal `IOutboxStorage` router. Public pipeline signatures do not change.

## Slices

1. **Slice 1 (this one):** core plumbing + PostgreSQL. Two PG providers (different schemas) publish
   and deliver independently; Redis stays on the default key.
2. **Slice 2:** Redis — keyed settings (`RedisOutboxSettings`, stream keys prefixed per provider),
   keyed commands and registration.
3. **Slice 3:** composite `(providerKey, groupId)` for `IConsumer<T>`/the manager if group uniqueness
   becomes a constraint; per-provider health.

## Invariants

- The guarantees are the same and **per provider**: at-least-once, ordering in `(tenant, part, type)`,
  DLQ/poison, lookback.
- No distributed transactions across providers, no load-based routing.
- Single-provider applications do not change: default key, publisher alias, previous sections.

## Tests

- two PG providers with different schemas: publish to each, independent delivery, independent
  migrations/cleanup;
- the default-key PG path behaves as before (the whole existing suite);
- a duplicate provider key and a duplicate consumer group id fail at startup;
- `IOutboxPublisherResolver.For(unknown)` — clear error;
- Redis (slice 1) — the existing suite stays green.

## Status

- **Slice 1 is implemented** (core + PostgreSQL).
- **Slice 2 is implemented** (Redis): settings, options and `WithOutboxSettings`/`WithConfiguration`
  actions are per provider (named options), `IDatabase` / `IRedisOutboxConnectionProvider` /
  connection options are keyed, commands and contracts are keyed, the poison handler is keyed with an
  unkeyed default and keyed precedence, and `IOutboxProviderRegistry` is keyed. For a non-default key
  the key prefix automatically gets a `:{providerKey}` suffix unless the configuration set its own, so
  two providers on one Redis database never collide. The one-call
  `AddSaOutboxUsingRedis(cfg, outbox, key)` overload was added.
- **Slice 3** (composite `(providerKey, groupId)` keys, per-provider health) is not started.

Constraints: tenants are shared per container; consumer group ids are unique across providers
(validated when the manager is built); provider-internal services are resolved keyed, so user
extensions registered as a type must use the factory overloads.
