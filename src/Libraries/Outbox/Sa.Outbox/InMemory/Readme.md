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
<<<<<<< HEAD
[`../PlugServices/Readme.md`](../PlugServices/Readme.md).
=======
[`../PlugServices/Readme.md`](../PlugServices/Readme.md). To build your own provider from this
example, follow [`../PlugServices/ProviderGuide.md`](../PlugServices/ProviderGuide.md).
>>>>>>> sdd

## Contents

- [Quick start](#quick-start)
<<<<<<< HEAD
=======
- [Step-by-step registration](#step-by-step-registration)
>>>>>>> sdd
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

<<<<<<< HEAD
=======
## Step-by-step registration

A runnable end-to-end version of these steps is
[`src/Samples/InMemoryOutbox.ConsoleApp`](../../../../Samples/InMemoryOutbox.ConsoleApp) — it publishes
before the host starts, then waits for the consumer to acknowledge everything.

### 1. Nothing to install

The provider ships inside the `Sa.Outbox` package (namespace `Sa.Outbox.InMemory`), so a project that
already references `Sa.Outbox` needs no extra package, connection string or configuration section.

### 2. Register the provider

One call registers the provider *and* the core:

```csharp
builder.Services.AddSaOutboxUsingInMemory(
    configure: settings => settings.Capacity = 1_000,        // 0 = unlimited (the default)
    configureOutbox: outbox => outbox
        .WithTenants((_, tenants) => tenants.WithTenantIds(1, 2)));
```

If the core is registered elsewhere, use the provider-only overload and give both calls the same
scope key:

```csharp
builder.Services
    .AddSaOutboxUsingInMemory(scopeKey: MyScope)
    .AddSaOutbox(ConfigureOutbox, MyScope);
```

`settings.Enabled = false` makes the call a no-op — no store, no contracts, no schedule — which is
the switch to use when the provider is optional per environment.

> `Sa.Outbox` defaults to `JsonOutboxMessageSerializer` (reflection-based JSON, not AOT-safe). In a
> Native AOT application add `.WithMessageSerializer(...)` with a source-generated context, exactly as
> the sample does.

### 3. Write a consumer

```csharp
public sealed class OrderConsumer(ILogger<OrderConsumer> logger) : IConsumer<OrderPlaced>
{
    public ValueTask Consume(
        OutboxConsumerSettings settings,
        OutboxMessageFilter filter,
        ReadOnlyMemory<IOutboxContextOperations<OrderPlaced>> messages,
        CancellationToken cancellationToken)
    {
        foreach (IOutboxContextOperations<OrderPlaced> message in messages.Span)
        {
            logger.LogInformation("Order {PayloadId}, tenant {Tenant}", message.PayloadId, filter.TenantId);

            message.Ok();   // omit this and a Pending message is still treated as delivered
        }

        return ValueTask.CompletedTask;
    }
}
```

### 4. Register the consumer group

```csharp
.WithDeliveries(deliveries => deliveries.AddDelivery<OrderConsumer, OrderPlaced>(
    "orders",                                   // group id → the DI key and the job name
    (_, consumer) => consumer
        .WithInterval(TimeSpan.FromMilliseconds(200))
        .WithNoBatchingWindow()
        .StartImmediately()))
```

`AddDelivery` keeps **one** consumer instance for the process; `AddDeliveryScoped` creates a fresh DI
scope per batch, which is what a consumer with a `DbContext` or a per-request dependency needs. Each
group becomes its own job in the outbox's `Sa.Schedule`.

### 5. Declare tenants and metadata

```csharp
.WithTenants((_, tenants) => tenants.WithTenantIds(1, 2))
.WithMetadata((_, metadata) => metadata.AddMetadata<OrderPlaced>("orders", order => order.Id))
```

Tenants are auto-detected by default: `InMemoryOutboxTenantDetector` reports the tenants that still
hold deliverable messages, so `WithTenantIds` is only needed to pin the list. `AddMetadata` sets the
part name (default `"root"`) and the payload id (default: `IOutboxPublishable.GetPayloadId()`).

### 6. Publish

```csharp
app.MapPost("/orders", async (OrderPlaced order, IOutboxMessagePublisher publisher, CancellationToken ct) =>
{
    await publisher.Publish([order], tenantId: 1, ct);
    return Results.Accepted();
});
```

`Publish` returns how many messages were stored and never talks to the network. Overloads that take
`Func<TMessage, int>` group the collection by tenant themselves.

### 7. Start the host

```csharp
await host.StartAsync();
// … the delivery job drains the store on its own schedule …
await host.StopAsync();
```

Publishing works before `StartAsync` (the store is just a dictionary); **delivery does not** — the
schedule is an `IHostedService`, and it is what makes the consumer run.

### 8. Verify the wiring

There is no health probe and no delivery log, so a missing registration looks like "nothing happens".
Check the registry instead of guessing:

```csharp
IOutboxConsumerManager manager = app.Services.GetRequiredService<IOutboxConsumerManager>();
OutboxConsumerSettings? settings = manager.Get("orders");   // null → the group was not registered
```

For behaviour, log inside the consumer (step 3) or assert on a signal as the sample does.

### 9. Swap in a real provider

Replace the registration call and keep everything else — consumers, tenants, metadata and publish
calls are provider-agnostic:

```csharp
builder.Services.AddSaOutboxUsingPostgreSql(pg => pg
    .WithDataSource(ds => ds.Options(o => o.Configure(c => c.ConnectionString = connectionString))));
```

Pending messages do **not** migrate between providers: drain the in-memory one before switching.

>>>>>>> sdd
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
