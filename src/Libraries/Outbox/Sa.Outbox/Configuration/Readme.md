# Configuration

**English** | [Русский](Readme-ru.md)

Consumer registration, consumption and publication settings, serialization, multi-tenancy and runtime management for the `Sa.Outbox` core. Part of the [`Sa.Outbox`](../Readme.md) documentation; storage and delivery are the provider's job — see [Available providers](../Readme.md#available-providers).

## Contents

- [Consumer Registration](#consumer-registration)
- [Runtime Settings Management](#runtime-settings-management)
- [Consumption Settings](#consumption-settings)
- [Empty-Queue Wait Policy](#empty-queue-wait-policy)
- [Global DI Overrides](#global-di-overrides)
- [Publish Settings](#publish-settings)
- [Serialization](#serialization)
- [Multi-Tenancy](#multi-tenancy)
- [Message Metadata](#message-metadata)

## Consumer Registration

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
see [`Configuration/Readme.md`](../../Sa.Outbox.PostgreSql/Configuration/Readme.md#pgoutboxdeliveryoptions)
for the PostgreSQL binding rules (nullable properties, precedence, what is deliberately excluded).

## Runtime Settings Management

`IOutboxConsumerManager` allows changing settings without restarting:

```csharp
// Atomic swap — new snapshot applied atomically
manager.Apply("orders", s => s with { MaxBatchSize = 64 });

// Pause / Resume
manager.Pause("orders");
manager.Unpause("orders");

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

## Consumption Settings

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

**`ConsumerGroupId` is an identity, not a label.** `AddDelivery` / `AddDeliveryScoped` use it verbatim
as the consumer's DI key, the settings key, the job name and part of the PostgreSQL partition name.
Only lowercase ASCII letters, digits and underscores are accepted; `es-group`, `ES_GROUP` and the
like throw `ArgumentException` instead of being silently rewritten to `es_group`, so two
registrations can never collapse into one group.

The same rule covers a name derived by `IConsumerGroupNamingStrategy` (the `AddDelivery` overloads
without an id): a strategy that returns `Orders-V2` also fails registration instead of becoming
`orders_v2`. The error names the strategy as the source.

## Empty-Queue Wait Policy

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

See [`Delivery/Readme.md`](../Delivery/Readme.md) → *Configuring the wait policy* for a custom
`IEmptyQueueWaitStrategy` implementation.

---

## Global DI Overrides

```csharp
builder.Services.AddSaOutbox(builder => builder
    .WithDeliveryBatcher<MyBatcher>()          // custom batch-size calculator
    .WithIdleWaitStrategy<MyIdleStrategy>()    // custom empty-queue wait strategy
);
```

Both are optional: `IDeliveryBatcher` defaults to `DeliveryBatcher` (passthrough of `MaxBatchSize`), and `IEmptyQueueWaitStrategy` defaults to `ExponentialIdleWaitStrategy`. The default idle strategy is inert until a group opts in with `WithIdleBackoff(maxInterval)`, so out-of-the-box behavior is a fixed `Interval` poll.

---

## Publish Settings

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

## Serialization

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

## Multi-Tenancy

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
[`PlugServices/Readme.md`](../PlugServices/Readme.md). Per-tenant parallelism is a consumer-group
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

## Message Metadata

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
