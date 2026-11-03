# InMemory: the reference provider

**English** | [Русский](Readme-ru.md)

An in-memory implementation of the outbox plug point that lives inside the `Sa.Outbox` package. It is
two things at once:

- a **development and test substitute** for a real store — no infrastructure, no connection string,
  no schema, so publish → rent → consume → acknowledge runs without Docker;
- the **reference implementation** of the plug point: the provider is a few hundred lines, readable
  in one sitting, and answers "what does a provider actually have to do?".

It is deliberately **not** a production provider — see [Limitations](#limitations). For the pipeline
it plugs into, see [`../Delivery/Readme.md`](../Delivery/Readme.md); for the contracts themselves,
[`../PlugServices/Readme.md`](../PlugServices/Readme.md).

## Contents

- [Quick start](#quick-start)
- [File map](#file-map)
- [Behaviour](#behaviour)
- [Cancellation and retries](#cancellation-and-retries)
- [Limitations](#limitations)

## Quick start

```csharp
services.AddSaOutboxUsingInMemory(
    configure: settings => settings.Capacity = 1_000,   // 0 = unlimited (the default)
    configureOutbox: builder => builder
        .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
        .WithDeliveries(deliveries => deliveries.AddDelivery<MyConsumer, MyMessage>("group")));
```

`AddSaOutboxUsingInMemory(configure, scopeKey)` is the single-provider form; the overload with
`configureOutbox` also registers the core. A disabled provider (`Enabled = false`) registers nothing:
no store, no contracts, no schedule.

## File map

| File | Responsibility |
|---|---|
| `InMemoryOutboxStore.cs` | The whole storage: an id map, a per-`(tenant, part, payload type)` ordered index, leases and the rent generation |
| `InMemoryOutboxBulkWriter.cs` | `IOutboxBulkWriter` — one method: serialize the batch, append it |
| `InMemoryOutboxDeliveryManager.cs` | `IOutboxDeliveryManager` — rent records, write the outcomes back, extend leases |
| `InMemoryOutboxTenantDetector.cs` | `IOutboxTenantDetector` — tenants that still hold deliverable messages |
| `InMemoryOutboxRecord.cs` | One stored message: payload plus status, attempt counter, lease and owner |
| `InMemoryOutboxSettings.cs` | `Enabled` and `Capacity`; deliberately nothing else |
| `Setup.cs` | `AddSaOutboxUsingInMemory` — descriptor, capabilities and the three contracts |

## Behaviour

| Aspect | Behaviour |
|---|---|
| Storage | `Dictionary<long, record>` + a per-`(tenant, part, payload type)` ordered index |
| Claiming | in-process lease with `LeaseExpiresAt`; an expired lease is rentable again |
| `Postpone`/`Retry` delay | honored through `AvailableAt` |
| Attempt counter | advanced on `Retry`/`Warn`, not on `Postpone` |
| Publish transaction | none — a non-null context throws `NotSupportedException` |
| Capacity | `InMemoryOutboxSettings.Capacity`; overflow throws `OutboxException`, default unlimited |
| Durability | **none** — a process restart loses every message |
| Cross-process delivery | **no** — one provider instance serves one process |
| Health / warm-up | none; a missing check is reported healthy |
| Poison policy | none — the status and message stay on the record |
| Declared capabilities | `TenantDetection`, `LockExtension`, `Postponement` |

## Cancellation and retries

This provider is where the whole cancellation story fits in about forty lines, so it doubles as the
readable reference for the two real ones. The pipeline-level rules — how a shutdown is told apart
from a consumer's own timeout, `PerTenantTimeout`, the at-least-once duplicate window — live in
[`../Delivery/Readme.md`](../Delivery/Readme.md#cancellation).

- **Publish is all-or-nothing per chunk.** `InMemoryOutboxBulkWriter` checks the token once, after
  the empty check; `Insert` is then a single mutation under the store lock. A token cancelled before
  that check stores nothing; one cancelled after it is not observed, because the rest of the call is
  synchronous. A publish larger than `MaxBatchSize` is still split into chunks by the core, so a
  cancellation between chunks keeps the chunks already inserted.
- **A cancelled pass leaves the lease, not a lost message.** `ReturnDelivery` is never called, so the
  records keep `Processing`, their `TransactId` and `LeaseExpiresAt`. Once the lease expires
  (`LockDuration`, 10 s by default) `Rent` treats them as rentable again.
- **Cancellation spends no attempt.** `Attempt` is advanced only by `Return`, and only for
  `Retry`/`Warn`, so a cancelled pass leaves the retry budget untouched — the opposite of Redis, where
  the rent itself is the counter.
- **No extra batching window.** A re-rented record and a `Warn` with zero delay become rentable as
  soon as `LeaseExpiresAt`/`AvailableAt` allows; PostgreSQL additionally requires
  `lock_expires_on < now - BatchingWindow`. The message's own `CreatedAt` must still be older than
  `ToDate = now - BatchingWindow` to be selected at all.
- **A stale outcome cannot settle a re-rented message.** `Return` drops every outcome whose
  `RentGeneration` (the second half of the rent token) or `TransactId` no longer matches the record,
  so a pass that comes back late — a consumer that swallowed the cancellation, for instance — cannot
  acknowledge a message another pass has taken.
- **A lost lease is reported, not hidden.** `ExtendDelivery` returns `0` once the lease expired or
  was taken over; the core reads that as a lost lock and re-delivers the message from its new owner.

> The store is process-local: a restart loses the messages themselves, so none of the above survives
> the process. Within its lifetime, cancellation only moves work in time.

## Limitations

- **No durability.** Everything, including `Pending` and unfinished `Processing` messages, is gone
  when the process exits.
- **No cross-process or cross-node delivery.** One provider instance serves one process, so it is
  not a substitute for a shared store in a scaled-out deployment.
- **No publish context.** There is no transaction to enlist into, so passing one throws
  `NotSupportedException` instead of silently ignoring the guarantee.
- **No persisted errors, poison queue, retention or trimming.** The final status and message stay on
  the record; `Capacity` is a fail-fast guard against unbounded growth, not retention.
- **No health probe, warm-up, migrations or partitioning** — there is no schema and nothing to
  migrate, and the provider reports itself healthy by not having a check.
- **Uses the core serializer.** Both contracts resolve the unkeyed `IOutboxMessageSerializer`, so a
  provider-specific keyed serializer does not apply here.

> The in-memory provider is **not** a production provider. It exists so the pipeline can be exercised
> without a database and so a provider author has a few-hundred-line example instead of a
> several-thousand-line one.
