# Sa.Outbox.Redis

A minimal Redis Streams implementation of the Outbox provider for `Sa.Outbox`. Same publish and
delivery contract as `Sa.Outbox.PostgreSql`, but the log, the consumer cursors and the retry state
live in Redis. There is no provider-side bookkeeping outside the stream: an entry that is not
acknowledged stays in the consumer group's pending entries list (PEL), and that list — together with
Redis's delivery counter — is the retry state.

No Lua: every operation is one or a few ordinary Redis commands, pipelined where they do not depend
on each other. Every command touches a single key, so the provider works on a Redis Cluster as is.

## Keys

| Key | Type | Purpose |
|---|---|---|
| `sa:outbox:stream:{tenant}:{part}:{type}` | stream | the message log of one (tenant, part, payload type) |
| `sa:outbox:tenants` | set | tenant ids that have ever published |

The consumer group itself is not a key: it lives inside the stream, and its name is the consumer
group id passed to `AddDelivery`/`AddDeliveryScoped`. Adding a group costs nothing — it reads the
same log with its own cursor.

## Quick start

```csharp
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.Publication;
using Sa.Outbox.Redis;

IHost host = Host.CreateDefaultBuilder()
    .ConfigureServices(services => services
        .AddSaOutbox(builder => builder
            .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
            .WithDeliveries(deliveries => deliveries
                .AddDeliveryScoped<OrderConsumer, OrderCreated>((_, group) => group
                    .WithInterval(TimeSpan.FromSeconds(5))
                    .StartImmediately()
                    .WithMaxBatchSize(16)
                    .WithLockDuration(TimeSpan.FromSeconds(10)))))
        .AddSaOutboxUsingRedis(cfg => cfg
            .WithConnectionString("localhost:6379")))
    .Build();

var publisher = host.Services.GetRequiredService<IOutboxMessagePublisher>();
await publisher.Publish([new OrderCreated("order-42")], tenantId: 1);

await host.RunAsync();
```

## Connection

Resolved lazily, on the first command, in this order:

1. `WithDatabase(IDatabase)` / `WithDatabase(Func<IServiceProvider, IDatabase>)`
2. `WithConnectionMultiplexer(IConnectionMultiplexer)` / its factory overload
3. an `IDatabase` already registered in the container (before `AddSaOutboxUsingRedis`)
4. an `IConnectionMultiplexer` already registered in the container
5. a connection string: `WithConnectionString("…")`, or the `ConnectionStrings:Redis` entry

Only the connection-string path creates the multiplexer, and only that path disposes it. A
multiplexer or database handed in by the application (or registered in DI) belongs to the caller and
is never disposed by the Outbox, so it can be shared with caching, SignalR, etc.

## Publish: the algorithm

```
Publish<TMessage>(messages, tenantId)
  1. metadata: part name (AddMetadata<TMessage>) + PayloadId + CreatedAt = TimeProvider.GetUtcNow()
  2. stream key = sa:outbox:stream:{tenantId}:{part}:{typeof(TMessage).Name}
  3. XADD each message (one pipelined IBatch, not MULTI/EXEC)
       i = PayloadId, t = typeof(TMessage).FullName, c = CreatedAt ms, p = serialized payload
  4. SADD sa:outbox:tenants {tenantId}
  5. retention trim (if enabled): throttled per stream, see Retention
```

1. `IOutboxMessagePublisher` takes part and payload id from the metadata registered with
   `AddMetadata<TMessage>`; `CreatedAt` comes from the ambient `TimeProvider`.
2. The stream is the unit of ordering: one stream per `(tenant, part, payload type)`. Type identity
   is the **simple** name (`typeof(T).Name`, matching `OutboxMessageFilter.PayloadType`). Two types
   from different namespaces sharing one simple name share a stream — give them different names or
   different parts.
3. Every message of the batch is an independent `XADD`, pipelined through `IBatch`. There is no
   transaction: each append is atomic on its own.
4. The tenant is added to the `tenants` set read by `IOutboxTenantDetector`.
5. If retention is enabled, the stream is trimmed right after the batch, subject to the per-stream
   `TrimInterval` throttle. The trim never touches pending or undelivered entries.

A publish context is **not** supported: `InsertBulk(..., context)` throws
`NotSupportedException` instead of silently dropping a caller's transaction. `Publish` returns the
number of appended entries.

## Delivery: the algorithm

```
Sa.Schedule job  (one per consumer group, Interval / InitialDelay / IdleMaxInterval)
  └─ DeliveryJob<TMessage>
      └─ DeliveryProcessor        loop: MaxProcessingIterations, IterationDelay
          ├─ SMEMBERS sa:outbox:tenants  → tenant ids
          └─ per tenant (sequential or PerTenantMaxDegreeOfParallelism)
              └─ DeliveryTenant.ProcessInTenant
                  ├─ filter: PayloadType=Name, TenantId, Part, ConsumerGroupId, TransactId,
                  │          FromDate = now - LookbackInterval, NowDate = now
                  ├─ batch size = clamp(batcher.CalculateBatchSize, 0, MaxBatchSize)
                  ├─ RentDelivery            ← XAUTOCLAIM / XPENDING / XREADGROUP  (below)
                  ├─ LockRenewer             → ExtendDelivery every LockRenewal (XCLAIM JUSTID)
                  ├─ IConsumer<TMessage>.Consume(...)  → context.Ok()/Warn()/Retry()/Error()…
                  ├─ ReturnDelivery          → XACK | leave pending | poison handler + XACK
                  └─ return contexts to the pool
```

1. The scheduler starts one `DeliveryJob<TMessage>` per consumer group. Its period is `Interval`,
   stretched by adaptive back-off on empty queues up to `IdleMaxInterval`; `Paused` skips the cycle.
2. `DeliveryProcessor` snapshots `OutboxConsumerSettings` once per run, reads the tenant list and
   repeats the loop while the previous iteration processed something, up to `MaxProcessingIterations`
   (with `IterationDelay` between iterations). Tenants run sequentially or in parallel according to
   `PerTenantMaxDegreeOfParallelism`.
3. `DeliveryTenant` builds the `OutboxMessageFilter` (the payload type is the same simple name used
   at publish), asks `IDeliveryBatcher` for the batch size and calls `RentDelivery`.
4. `RentDelivery` fills the buffer (details below). Everything it returns is already in the group's
   PEL under the call's `TransactId`.
5. While the consumer works, `LockRenewer` calls `ExtendDelivery` every `LockRenewal`, resetting the
   idle clock so a long batch is not stolen.
6. `ReturnDelivery` applies the outcome: `XACK` on success, nothing on retryable results (the entry
   stays pending), poison handler + `XACK` on permanent failure.

### Rent: one poll

1. `XGROUP CREATE … MKSTREAM` on first use (a `BUSYGROUP` reply is fine), at the position selected
   by `ConsumeSettings.StartFrom` (`Lookback` = `FromDate`, `Beginning` = `0-0`, `Now` = `$`). The
   start position only matters when the group is created; changing it later does not move it.
2. `XAUTOCLAIM <group> <consumer> <PendingClaimIdle> <cursor>` walks the PEL page by page and claims
   entries idle for at least `PendingClaimIdle` (10 minutes by default). The `NextStartId` of the
   result is passed to the next call, so one poll scans the whole PEL without revisiting entries.
   The consumer name is the call's `TransactId`.
3. `XPENDING` reads the delivery counter of the claimed ids. An entry whose counter exceeds
   `ConsumeSettings.MaxDeliveryAttempts` is handed to `IRedisOutboxPoisonHandler` and acknowledged
   instead of being delivered again — this is the poison-message guard.
4. Remaining slots are filled with `XREADGROUP … >` — entries no group member has read yet
   (`deliveryCount = 1`, `Attempt = 0`).
5. Each entry becomes an `IOutboxContextOperations<TMessage>`: `OutboxId` is the stream id packed
   into a `Guid`, `TaskId` is derived from the same pair, `Attempt = deliveryCount - 1`, payload is
   deserialized with the core `IOutboxMessageSerializer` (`Sa.Outbox.Serialization`; the default is UTF-8 JSON).

### Finish: appending the outcome

- success (`Ok`, `Created`, `301`, …) → `XACK`;
- `Postpone`/`Retry`/`Warn`/`Pending` → no command at all: the entry stays pending and is claimed
  again after `PendingClaimIdle`;
- `Error`/`Error5xx`/`MaximumAttemptsError` → poison handler, then `XACK`.

`Postpone`/`Retry` do not carry a due date here: the stream-only model has no per-entry timer, so the
retry granularity is `PendingClaimIdle`.

### Extend: keeping a batch alive

`XCLAIM <stream> <group> <consumer> 0 <id> JUSTID` for the call's pending entries: it resets their
idle time without incrementing the delivery counter.

`DeliveryInfo.Attempt` is the pending entry's delivery count minus one: `0` on the first rent, `1`
after the first reclaim, and so on. The provider hands an entry to the poison handler once its
delivery count exceeds `ConsumeSettings.MaxDeliveryAttempts`; the consumer-level
`MaxDeliveryAttempts` still applies to outcomes reported through `ReturnDelivery`, exactly as in the
PostgreSQL provider.

`OutboxId` is the stream id packed into the 16 bytes of a `Guid` (8 bytes of milliseconds + 8 bytes
of sequence, big-endian); `TaskId` is derived from the same pair. There is no Guid → stream-id
index — use `PayloadId` for the business key.

## Settings

The settings object is a standard `Microsoft.Extensions.Options` instance. Three layers are applied
in order, each overriding the previous one:

1. code defaults passed to `WithOutboxSettings`;
2. the configuration section passed to `WithConfiguration`;
3. raw `Options(...)` actions.

Validation runs when the settings are first resolved, and at host start when the container validates
options (`ValidateOnStart`). An invalid value surfaces as an `OptionsValidationException`.

```csharp
cfg.WithConfiguration("Sa:Outbox:Redis");

cfg.WithOutboxSettings((sp, settings) =>
{
    settings.KeySettings.Prefix = "sa:outbox";
    settings.KeySettings.Separator = ":";

    settings.ConsumeSettings.StartFrom = RedisOutboxGroupStart.Lookback; // Beginning | Now
    settings.ConsumeSettings.PendingClaimIdle = TimeSpan.FromMinutes(10);
    settings.ConsumeSettings.MaxDeliveryAttempts = 5;
});

// Escape hatch over both: the last word on the standard options pipeline.
cfg.Options(options => options.Configure(settings => settings.RetentionSettings.Enabled = true));
```

```json
{
  "Sa": {
    "Outbox": {
      "Redis": {
        "KeySettings": { "Prefix": "sa:outbox", "Separator": ":" },
        "ConsumeSettings": {
          "StartFrom": "Lookback",
          "PendingClaimIdle": "00:10:00",
          "MaxDeliveryAttempts": 5
        },
        "RetentionSettings": {
          "Enabled": true,
          "StreamMaxAge": "7.00:00:00",
          "TrimInterval": "00:05:00",
          "Approximate": true,
          "TrimLimit": 10000
        }
      }
    }
  }
}
```

`StartFrom` only matters when a group is created on an existing stream: `Lookback` starts at
`OutboxMessageFilter.FromDate` (the group's lookback window), `Beginning` at `0-0`, `Now` at `$`.

`PendingClaimIdle` is the only retry clock: an entry that was not acknowledged — a failure, a
postponement, a crashed worker — becomes claimable again after it has been idle that long. Keep it
comfortably above the consumer's `LockDuration`. `MaxDeliveryAttempts` bounds how many times such an
entry may be claimed before it is handed to the poison handler.

### Retention

Automatic trimming is **off by default**. When `Enabled` is set, it runs inline after a publish
batch, throttled per stream by `TrimInterval` (5 minutes by default) — a stream that stops growing is
simply not trimmed.

A message stream is never trimmed by raw length. The provider computes a safe floor from the groups
of the stream (`XINFO GROUPS`, and `XPENDING` when a group has pending entries):

- a group with pending entries contributes its oldest pending id;
- otherwise it contributes its `last-delivered-id` (everything before it was delivered and
  acknowledged);
- a group that has consumed nothing yet (`last-delivered-id = 0-0`) blocks trimming, because it may
  still start at the beginning.

The floor is the smallest contribution across all groups, and the removed range is
`min(safeFloor, now - StreamMaxAge)`: an entry must be older than the retention window **and**
needed by no group. `StreamMaxAge = 0` disables the age limit but keeps the safe floor. A stream with
no group is not trimmed at all, so a group created later with `StartFrom = Beginning` starts on a
complete log.

`Approximate = true` uses `XTRIM ... ~`, which is much cheaper for Redis and may leave slightly more
entries than the limit. `TrimLimit` maps to `XTRIM ... LIMIT` and requires the approximate mode
(a non-zero `TrimLimit` with `Approximate = false` fails validation); `0` removes the limit.

### Poison messages

There is no dead-letter stream. When the provider stops delivering a message — its pending delivery
counter exceeded `ConsumeSettings.MaxDeliveryAttempts`, or the consumer reported a permanent outcome
— it calls `IRedisOutboxPoisonHandler` and, if the handler returns normally, acknowledges the entry.

The default handler logs a warning and drops the message. Replace it to persist the message yourself:

```csharp
cfg.WithPoisonHandler<MyPoisonHandler>();          // resolved from DI
cfg.WithPoisonHandler(new MyPoisonHandler());      // instance
cfg.WithPoisonHandler(sp => new MyPoisonHandler(sp.GetRequiredService<MyStore>()));
```

```csharp
public sealed class MyPoisonHandler(MyStore store) : IRedisOutboxPoisonHandler
{
    public ValueTask Handle(RedisOutboxPoisonMessage message, CancellationToken cancellationToken)
        => store.Save(message.PayloadId, message.PayloadType, message.StatusCode,
                      message.Exception, message.Payload, cancellationToken);
}
```

The handler runs **before** `XACK`. Returning normally means the provider may acknowledge; throwing
means the entry is left pending and will be claimed again, so handling is at-least-once and the
handler must tolerate duplicates. The entry body itself stays in the message stream until retention
trims it, so even a dropped message can still be inspected with `XRANGE` for a while.

## Several providers in one container

`AddSaOutboxUsingRedis` takes a provider key (also the `scopeKey`), so one container can host several
Redis outboxes — each with its own connection, settings, schedule and consumer groups:

```csharp
services
    .AddSaOutboxUsingRedis(
        "cache",                                     // provider key
        r => r.WithConnectionString("cache"),
        outbox => outbox.WithDeliveries(d => d.AddDelivery<CacheConsumer, CacheEvent>("cache_events")))
    .AddSaOutboxUsingRedis(
        "billing",
        r => r.WithConnectionString("billing"),
        outbox => outbox.WithDeliveries(d => d.AddDelivery<InvoiceConsumer, Invoice>("invoices")));

var cache = sp.GetRequiredService<IOutboxPublisherResolver>().For("cache");
```

- Settings, named options, `IDatabase` and every command are per provider; `WithOutboxSettings` /
  `WithConfiguration` apply to their own key only.
- A provider registered under a non-default key without its own prefix automatically gets a
  `:{providerKey}` suffix (`sa:outbox:cache:...`), so two providers on one Redis database never
  collide.
- Without a key (`AddSaOutboxUsingRedis(cfg)`) everything behaves as before: default key `outbox`,
  prefix `sa:outbox`, unkeyed aliases for `IDatabase`/settings/publisher.
- The startup `RedisMultiProviderGuard` fails when two providers point at the same endpoint and
  database with the same prefix (every message would be delivered twice); an explicit
  `IDatabase`/`IConnectionMultiplexer` factory is not checked — its endpoint is unknown until it runs.
- Tenants are shared per container, and consumer group ids may repeat (composite keys).
## Migrating 0.1 → 0.2

- **Nothing in Redis is migrated.** The default key (`AddSaOutboxUsingRedis(cfg)`) keeps the previous
  `sa:outbox` prefix; streams, groups and the PEL are read as before.
- **Moving to a non-default provider key** changes the keys: the provider gets a `:{key}` suffix
  (`sa:outbox:billing:...`). To continue with the old keys, set the prefix explicitly:
  `WithOutboxSettings((_, s) => s.KeySettings.Prefix = "sa:outbox")`. Otherwise these are new, empty
  streams.
- **Registration**: the one-call `AddSaOutboxUsingRedis(cfg, outbox, key)` overload; settings and
  commands are keyed, and the default key keeps its unkeyed aliases (`IDatabase`,
  `RedisOutboxSettings`, the publisher).
- **0.18 contracts**: `IOutboxBulkWriter` has one batch method, `OutboxDeliveryMessage<T>` is a flat
  record with a `RentToken` (see `Sa.Outbox/PlugServices/Readme.md`).


## Limitations

- Retention is inline and per stream; a stream that stops receiving publishes is not trimmed (it is
  also not growing). Trimming never removes entries a group still needs, but it is not a substitute
  for `maxmemory` as a hard upper bound.

- No publish context/transaction; see above.
- Retry timing is coarse: a failed or postponed message is redelivered after `PendingClaimIdle`, not
  after the delay passed to `Postpone`/`Retry`. Every claim also counts toward the provider's
  `MaxDeliveryAttempts`, postponements included.
- Delivery is at-least-once: an entry idle longer than `PendingClaimIdle` while a batch is still
  running is re-delivered. Consumers must be idempotent.
- Ordering is per stream — per `(tenant, part, payload type)`, not global.
