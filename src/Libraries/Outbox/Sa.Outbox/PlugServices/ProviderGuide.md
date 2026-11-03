# Implementing a provider

**English** | [Русский](ProviderGuide-ru.md)

A step-by-step guide to plugging your own storage into `Sa.Outbox`, using the in-memory provider
that ships in this package as the worked example. The reference sources are
[`../InMemory/`](../InMemory/) — the guide quotes them, trimmed, so you can read the two side by side.

The contracts themselves are described in [`Readme.md`](Readme.md); this file is about writing them.

## Contents

- [What you write and what you get](#what-you-write-and-what-you-get)
- [1. The shape of a provider](#1-the-shape-of-a-provider)
- [2. State: one record, one index](#2-state-one-record-one-index)
- [3. `IOutboxBulkWriter` — storing a batch](#3-ioutboxbulkwriter--storing-a-batch)
- [4. `IOutboxDeliveryManager` — rent, return, extend](#4-ioutboxdeliverymanager--rent-return-extend)
- [5. `IOutboxTenantDetector` — optional tenant discovery](#5-ioutboxtenantdetector--optional-tenant-discovery)
- [6. Registration](#6-registration)
- [7. What the core already does for you](#7-what-the-core-already-does-for-you)
- [8. Contract checklist](#8-contract-checklist)
- [9. Testing your provider](#9-testing-your-provider)
- [10. Capabilities and several providers](#10-capabilities-and-several-providers)

## What you write and what you get

| You implement | The core gives you |
|---|---|
| `IOutboxBulkWriter` — persist one batch | chunking by `MaxBatchSize`, payload id resolution, the whole publish API |
| `IOutboxDeliveryManager` — rent a batch, write its outcome, renew a lease | batching, tenant iteration, the lock-renewal loop, cancellation, retry/back-off policy, `MaxDeliveryAttempts` → `508`, status semantics, the schedule |
| `IOutboxTenantDetector` — optional | fan-out over tenants |

A provider is **storage only**. Nothing about retries, statuses or consumers belongs in it — see
[§7](#7-what-the-core-already-does-for-you).

## 1. The shape of a provider

Four files are enough; the in-memory provider has exactly this layout:

```
MyProvider/
├── MyOutboxStore.cs              state: records, index, leases
├── MyOutboxBulkWriter.cs        IOutboxBulkWriter
├── MyOutboxDeliveryManager.cs    IOutboxDeliveryManager
├── MyOutboxTenantDetector.cs    IOutboxTenantDetector (optional)
└── Setup.cs                     AddOutboxProvider(...)
```

The provider can live in the application assembly; a separate NuGet package is only worth it when
others will use it. Everything you need is public: the contracts, `OutboxMessageBatch`,
`OutboxDeliveryMessage`, `OutboxRentToken`, `IOutboxContextFactory`, `OutboxPayloadCodec`,
`OutboxProviderDescriptor`.

## 2. State: one record, one index

The in-memory record is the shape every provider ends up with — message, delivery state, lease and
owner in one row:

```csharp
internal sealed class MyOutboxRecord
{
    public long Id { get; set; }                        // your row id → RentToken.Key
    public Guid OutboxId { get; init; }                 // public identity
    public int TenantId { get; init; }
    public string Part { get; init; } = "";             // message part (metadata)
    public string PayloadType { get; init; } = "";      // Type.Name, what the filter matches on
    public string PayloadId { get; init; } = "";        // application key
    public byte[] Payload { get; init; } = [];          // serialized
    public DateTimeOffset CreatedAt { get; init; }

    public DeliveryStatusCode Status { get; set; } = DeliveryStatusCode.Pending;
    public int Attempt { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }     // after Postpone/Retry/Warn
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public long RentGeneration { get; set; }             // RentToken.SubKey
    public string? TransactId { get; set; }              // current owner
}
```

> A real store usually splits this into a message table and a per-consumer-group task table (that is
> what PostgreSQL does); a simple store can keep one row per message and serve every group from it.

## 3. `IOutboxBulkWriter` — storing a batch

The only required contract. One batch has one `PartInfo` (tenant, part, timestamp), so resolution
happens once:

```csharp
internal sealed class MyOutboxBulkWriter(MyOutboxStore store, IOutboxMessageSerializer serializer)
    : IOutboxBulkWriter
{
    public ValueTask<ulong> InsertBulk<TMessage>(
        OutboxMessageBatch<TMessage> batch,
        object? context,
        CancellationToken cancellationToken = default)
    {
        // There is no transaction to enlist into. Reject rather than silently ignore a
        // transactional guarantee the caller expects; declare PublishContext only if you honour it.
        if (context is not null)
            throw new NotSupportedException("This provider has no publish context.");

        if (batch.IsEmpty) return ValueTask.FromResult(0UL);

        // Check the token before the first write: a provider whose commands cannot be cancelled
        // would otherwise store a batch the caller already abandoned.
        cancellationToken.ThrowIfCancellationRequested();

        List<MyOutboxRecord> records = new(batch.Count);

        foreach (OutboxMessageBatchItem<TMessage> item in batch.Messages.Span)
        {
            records.Add(new MyOutboxRecord
            {
                TenantId = batch.PartInfo.TenantId,
                Part = batch.PartInfo.Part,
                PayloadType = typeof(TMessage).Name,
                PayloadId = item.PayloadId,
                Payload = OutboxPayloadCodec.Serialize(serializer, item.Payload),
                CreatedAt = batch.PartInfo.CreatedAt,
            });
        }

        return ValueTask.FromResult(store.Insert(records));   // ulong: how many are stored
    }
}
```

Rules:

- return the number of messages actually stored (`ulong`), not bytes and not "1";
- store the batch all-or-nothing where your storage allows it — the core splits a publish into
  chunks and treats a chunk as one unit;
- `PayloadType` must be `typeof(TMessage).Name` (that is what `OutboxMessageFilter.PayloadType`
  carries);
- `OutboxPayloadCodec` is a convenience (pooled stream → exact-size array). Streaming straight into
  your writer is fine and avoids the copy.

## 4. `IOutboxDeliveryManager` — rent, return, extend

This is the only contract with semantics the core depends on. The in-memory implementation is short
enough to read whole:

```csharp
internal sealed class MyOutboxDeliveryManager(
    MyOutboxStore store,
    IOutboxContextFactory contextFactory,
    IOutboxMessageSerializer serializer) : IOutboxDeliveryManager
{
    public Task<int> RentDelivery<TMessage>(
        Memory<IOutboxContextOperations<TMessage>> writeBuffer,
        TimeSpan lockDuration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        // A cancelled cycle must open no lease at all.
        if (cancellationToken.IsCancellationRequested || writeBuffer.IsEmpty)
            return Task.FromResult(0);

        List<MyOutboxRecord> rented = store.Rent(lockDuration, filter, writeBuffer.Length);

        for (int i = 0; i < rented.Count; i++)
        {
            writeBuffer.Span[i] = contextFactory.Create<TMessage>(
                new OutboxDeliveryMessage<TMessage>(
                    OutboxId: rented[i].OutboxId,
                    PayloadId: rented[i].PayloadId,
                    Payload: OutboxPayloadCodec.Deserialize<TMessage>(serializer, rented[i].Payload)!,
                    PartInfo: new OutboxPartInfo(rented[i].TenantId, rented[i].Part, rented[i].CreatedAt),
                    // The task part is (tenant, consumer group, task time) — not the message part.
                    DeliveryInfo: new OutboxTaskDeliveryInfo(
                        TaskId: rented[i].Id,
                        DeliveryId: rented[i].RentGeneration,
                        Attempt: rented[i].Attempt,
                        LastErrorId: 0,
                        Status: default,
                        PartInfo: new OutboxPartInfo(rented[i].TenantId, filter.ConsumerGroupId, filter.NowDate)),
                    RentToken: new OutboxRentToken(rented[i].Id, rented[i].RentGeneration)));
        }

        return Task.FromResult(rented.Count);
    }

    public Task<int> ReturnDelivery<TMessage>(
        ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
    {
        List<MyOutboxOutcome> outcomes = new(messages.Length);

        foreach (IOutboxContextOperations<TMessage> context in messages.Span)
        {
            if (context.RentToken.IsNone) continue;

            outcomes.Add(new MyOutboxOutcome(
                context.RentToken.Key, context.RentToken.SubKey,
                context.DeliveryResult, context.PostponeDelay));
        }

        return Task.FromResult(store.Return(outcomes, filter));
    }

    public Task<int> ExtendDelivery(
        TimeSpan lockExpiration,
        OutboxMessageFilter filter,
        CancellationToken cancellationToken)
            => Task.FromResult(store.Extend(lockExpiration, filter));
}
```

with one small type carrying a single outcome:

```csharp
internal readonly record struct MyOutboxOutcome(
    long Id, long Generation, DeliveryStatus Status, TimeSpan Delay);
```

`Generation` is what makes rule #6 in [§8](#8-contract-checklist) enforceable: an outcome whose
generation no longer matches the record belongs to a lease that was already taken.

What the store has to guarantee:

| Call | Must |
|---|---|
| `RentDelivery` | Select messages of **`filter.ConsumerGroupId` + `filter.TenantId` + `filter.Part` + `filter.PayloadType`** that are rentable, mark them owned with a deadline, return the count and fill the buffer in that order |
| `RentDelivery` | Treat `Processing` with an expired lease as rentable — that is how a crashed or cancelled pass recovers |
| `ReturnDelivery` | Write the consumer's verdict from `context.DeliveryResult`, bump `Attempt` for `Retry`/`Warn` (**not** for `Postpone`), set the rentable-again time from `context.PostponeDelay`, return how many outcomes were applied |
| `ReturnDelivery` | Ignore an outcome whose rent is stale (another pass re-rented the message): compare `context.RentToken` with the current owner/generation instead of overwriting |
| `ExtendDelivery` | Renew the lease of the messages owned by `filter.TransactId`, return **how many were actually renewed**; `0` means the lease is gone and the core stops the batch |

Keep the rent token stable and opaque: it is the only thing that comes back on return/extend. The
in-memory provider uses `(row id, rent generation)`; PostgreSQL uses `(task id, delivery id)`, Redis
packs the stream id.

## 5. `IOutboxTenantDetector` — optional tenant discovery

Only needed when you declare `OutboxProviderCapabilities.TenantDetection` (or when the application
does not list tenants explicitly):

```csharp
internal sealed class MyOutboxTenantDetector(MyOutboxStore store) : IOutboxTenantDetector
{
    public bool CanDetect => true;

    public ValueTask<int[]> GetTenantIds(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(store.GetTenantIds());   // tenants with deliverable messages
    }
}
```

Return only tenants that still hold work: the loop iterates every id it gets, and an id that never
returns anything costs a `RentDelivery` per pass.

## 6. Registration

Wrap `AddOutboxProvider` in the usual `AddSaOutboxUsing...` shape:

```csharp
public static IServiceCollection AddSaOutboxUsingMyStore(
    this IServiceCollection services,
    Action<MyOutboxOptions>? configure = null,
    Action<IOutboxBuilder>? configureOutbox = null,
    string scopeKey = OutboxScope.DefaultKey)
{
    OutboxScope.EnsureValid(scopeKey);

    MyOutboxOptions options = new();
    configure?.Invoke(options);
    options.Validate();

    // The core publisher requires a TimeProvider and the core never registers one.
    services.TryAddSingleton(TimeProvider.System);

    services.TryAddKeyedSingleton(scopeKey, (sp, _) => new MyOutboxStore(
        sp.GetRequiredService<TimeProvider>(), options));

    services.AddSaOutbox(configureOutbox, scopeKey);

    return services.AddOutboxProvider(
        scopeKey,
        new OutboxProviderDescriptor(
            "MyStore",                                             // stable, human-facing
            OutboxProviderCapabilities.TenantDetection
            | OutboxProviderCapabilities.LockExtension
            | OutboxProviderCapabilities.Postponement),
        provider => provider
            .WithBulkWriter(sp => new MyOutboxBulkWriter(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey),
                sp.GetRequiredService<IOutboxMessageSerializer>()))
            .WithDeliveryManager(sp => new MyOutboxDeliveryManager(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey),
                sp.GetRequiredService<IOutboxContextFactory>(),
                sp.GetRequiredService<IOutboxMessageSerializer>()))
            .WithTenantDetector(sp => new MyOutboxTenantDetector(
                sp.GetRequiredKeyedService<MyOutboxStore>(scopeKey))));
}
```

Notes:

- **Factories, not types.** A keyed registration of a concrete type resolves its constructor
  parameters *unkeyed*, so provider services are registered through factories that pull their own
  keyed dependencies.
- **`scopeKey` is the provider key.** It must match the `scopeKey` passed to `AddSaOutbox`; the
  delivery pipeline carries it as `OutboxConsumerSettings.ProviderKey`.
- **`WithBulkWriter` is required** — `AddOutboxProvider` throws without it.
  `WithDeliveryManager` is required as soon as the application registers consumer groups;
  `WithTenantDetector` is required when `TenantDetection` is declared. The checks run at host start
  (`OutboxProviderValidationHostedService`), so a missing piece fails the boot, not the first message.
- A disabled provider should register **nothing** and return early — that is what the shipped
  providers do with `Enabled = false`.

## 7. What the core already does for you

Do **not** implement these in the provider:

| Behaviour | Who owns it |
|---|---|
| Chunking a publish by `MaxBatchSize`, resolving payload ids from metadata | `OutboxMessagePublisher` |
| Iterating tenants, sizing batches, iteration budget, pause | `DeliveryProcessor` |
| Invoking the consumer, turning a throw into `Warn` + back-off, cancellation handling | `DeliveryCourier` |
| `Attempt + 1 > MaxDeliveryAttempts` → `MaximumAttemptsError` (508) | `DeliveryCourier.PostHandle` |
| Renewing the lease while the consumer works, and failing on `renewed == 0` | the tenant pass + the core's lock renewer |
| `Retry`/`Postpone` delay computation for failures | `IDeliveryBackoffStrategy` |
| Schedule, adaptive idle, concurrency | `Sa.Schedule` |

Your job is one sentence: **make rent/return/extend durable and correct under concurrency**.

## 8. Contract checklist

| # | Rule | Why |
|---|---|---|
| 1 | `RentDelivery` returns `0` when the token is already cancelled | a cancelled pass must not create a lease it will never return |
| 2 | A message stays rentable after a crash or cancellation, once its lease expires | at-least-once recovery |
| 3 | `ReturnDelivery` bumps `Attempt` for `Retry`/`Warn` only | `Postpone` is consumer-governed and must not exhaust `MaxDeliveryAttempts` |
| 4 | Terminal statuses (2xx, 5xx, `508`) leave the rentable set; `Postpone`/`Retry`/`Warn` re-enter it at `now + PostponeDelay` | the status scale is the core's contract |
| 5 | `ExtendDelivery` returns the count actually renewed; `0` means the lease is lost | the core raises `LockRenewalException` and stops the batch |
| 6 | A stale outcome never overwrites a newer rent | otherwise a late worker acknowledges someone else's batch |
| 7 | The rent token is stable and lets you find the message again | it is the only identity returned to you |
| 8 | Publish checks cancellation before the first write and returns the stored count | the caller must not get success for work that never happened |
| 9 | `PayloadType` is `typeof(TMessage).Name` | the filter matches on exactly that |
| 10 | No cross-message dedupe is attempted | the outbox is at-least-once by design |

## 9. Testing your provider

Two layers, both cheap:

1. **Contract-level, no host:** build a container with `AddSaOutboxUsingMyStore()`, resolve
   `IOutboxBulkWriter` / `IOutboxDeliveryManager` keyed by the scope, and drive them directly —
   publish, rent, return, extend, recycle. This is exactly what
   [`../../../../Tests/Sa.Outbox.Tests/InMemory/`](../../../../Tests/Sa.Outbox.Tests/InMemory/) does;
   it runs in milliseconds and covers every rule in §8.
2. **End-to-end:** start a real `IHost`, publish, and let the schedule deliver — see
   [`../../../../Samples/InMemoryOutbox.ConsoleApp/`](../../../../Samples/InMemoryOutbox.ConsoleApp).

The clock: take `TimeProvider` from DI for both publishing and leases, so tests can move time instead
of sleeping.

## 10. Capabilities and several providers

| Capability | Declare it when | If you don't |
|---|---|---|
| `PublishContext` | `InsertBulk` really enlists into the caller's context | a non-null context must be rejected |
| `TenantDetection` | you register `IOutboxTenantDetector` | the host fails at start if the flag is set but the service is missing |
| `LockExtension` | `ExtendDelivery` actually renews | the core still calls it; renewing nothing stops the batch |
| `Postponement` | you honour the consumer's `Postpone`/`Retry` delay | the core still applies its own back-off for failures |

Several providers coexist when each is registered under its own `scopeKey`; the same key registered
twice with a different `Name` is an error, and so is one provider per key. All the provider's own
services should be keyed the same way, and the factories in §6 are what makes that work.

## See also

- [`Readme.md`](Readme.md) — the plug point, the contracts and their XML docs
- [`../InMemory/Readme.md`](../InMemory/Readme.md) — the reference implementation, with cancellation
  and retry semantics spelled out
- [`../Delivery/Readme.md`](../Delivery/Readme.md#cancellation) — what the pipeline does with the
  statuses you write
