# Delivery

## Overview

This folder contains the entire outbox **delivery** (consume) side: from the hosted job that wakes a consumer group up, down to the per-message status bookkeeping and the retry/DLQ decisions. It is storage-agnostic — the SQL lives in `Sa.Outbox.PostgreSql`, and this folder only talks to `IOutboxDeliveryManager` / `IOutboxContextFactory`.

The whole pipeline is three layers:

| Layer | Type | Responsibility |
|---|---|---|
| Loop | `DeliveryProcessor` | Iterates tenants until the backlog drains or the iteration budget runs out |
| Tenant pass | `DeliveryTenant` | Rents a batch, keeps it locked, hands it to the consumer, writes results back |
| Message | `DeliveryCourier` | Invokes the consumer, converts exceptions into statuses, applies the acceptance policy |

## File structure

| File | Responsibility |
|---|---|
| `IDeliveryBuilder.cs` / `DeliveryBuilder.cs` | Fluent registration of consumer groups (`AddDelivery` / `AddDeliveryScoped`) |
| `Job/Setup.cs` | `AddDeliveryJob` — builds settings, registers the keyed consumer, wires the Sa.Schedule job |
| `Job/DeliveryJob.cs` | `DeliveryJob<TMessage>` — the `IJob` entry point; resolves settings by job name, calls `ProcessMessages` and reports the run outcome to the idle state |
| `Job/DeliveryJobInterceptor.cs` | Sa.Schedule interceptor hooked into every delivery job |
| `IDeliveryProcessor.cs` / `DeliveryProcessor.cs` | The greedy loop over tenants; owns iteration and batch bookkeeping |
| `IDeliveryTenant.cs` / `DeliveryTenant.cs` | One tenant pass: build filter → size the batch → rent → deliver → return |
| `IDeliveryCourier.cs` / `DeliveryCourier.cs` | Invokes the consumer, converts exceptions to `Warn`, applies the post-handle policy |
| `IDeliveryLifetimeInvoker.cs` / `DeliveryLifetimeInvoker.cs` | Resolves `IConsumer<TMessage>` — one cached singleton instance, or a fresh DI scope per batch |
| `IDeliveryBatcher.cs` / `DeliveryBatcher.cs` | Default batch-size calculator (passthrough stub returning `MaxBatchSize`) |
| `DeliveryStatusCode.cs` | The status scale plus the `IsPending` / `IsSuccess` / `IsWarning` / `IsError` classifiers |
| `DeliveryStatus.cs` | `readonly record struct` describing one delivery attempt outcome |
| `OutboxContext.cs` | `IOutboxContextOperations<TMessage>` — the API a consumer uses to accept, retry or reject a message |
| `IOutboxContextFactory.cs` / `OutboxContextFactory.cs` | Creates contexts for rented rows |
| `OutboxConsumerSettings.cs` | Immutable settings snapshot (`record`) — the single source of truth for a consumer group |
| `OutboxConsumerSettingsBuilder.cs` | Fluent builder used both at bootstrap and for partial runtime updates |
| `OutboxDefaults.cs` | Default value for every setting, in one place |
| `IOutboxConsumerManager.cs` / `OutboxConsumerManager.cs` | Thread-safe runtime registry: register / apply / pause / resume / subscribe |
| `IDeliverySnapshot.cs` / `DeliverySnapshot.cs` | Lazily materialised view of jobs, message parts and consumer settings (used by the outbox load step) |
| `IDeliveryBackoffStrategy.cs` / `ExponentialDeliveryBackoffStrategy.cs` | Default backoff with jitter: `min(maxDelay, baseDelay * 2^(attempt-1)) * jitter` |
| `IEmptyQueueWaitStrategy.cs` / `FixedIntervalWaitStrategy.cs` / `ExponentialIdleWaitStrategy.cs` | Wait between polls of an empty queue: fixed (default) or exponential with jitter |
| `OutboxIdleState.cs` | Per-group consecutive-empty counter feeding the adaptive schedule |
| `AdaptiveIdleTiming.cs` | `IJobTiming` reading `OutboxIdleState.CurrentInterval` for the next occurrence |
| `OutboxIdleStateRegistry.cs` | Registry of `OutboxIdleState` per consumer group (interface + default impl) |
| `FilterFactory.cs` | Builds an `OutboxMessageFilter` from settings |
| `OutboxDeliveryMessage.cs` | A rented row: message payload plus delivery info |
| `OutboxTaskDeliveryInfo.cs` | Task-level delivery info (`TaskId`, `DeliveryId`, `Attempt`, `LastErrorId`, `Status`) |
| `IConsumerGroupNamingStrategy.cs` | Consumer-group name sanitisation strategy |
| `Setup.cs` | `AddOutboxDelivery` — DI wiring for everything above |

## The delivery pipeline

1. **`DeliveryJob<TMessage>.Execute`** resolves `OutboxConsumerSettings` from `IOutboxConsumerManager` by job name and calls `IDeliveryProcessor.ProcessMessages`. A missing registration is a hard error, not a silent no-op. After the run it reports the handled count to the group's `OutboxIdleState`, which the adaptive timing reads on the next iteration (see *Empty-queue back-off* below).
2. **`DeliveryProcessor.ProcessMessages`** runs the greedy loop. Each iteration: optionally delay `IterationDelay`, process every tenant, then ask `ShouldContinueProcessing`. When the group is `Paused` the loop waits 5 s and returns instead of polling.
3. **`DeliveryTenant.ProcessInTenant`** builds the filter, asks `IDeliveryBatcher` for a size, rents a `MemoryPool` buffer, calls `RentDelivery` (which flips rows to `Processing` under `FOR UPDATE SKIP LOCKED`), starts a `LockRenewer` on `LockRenewal`, delivers, then calls `ReturnDelivery`.
4. **`DeliveryCourier.Deliver`** calls the consumer, converts a thrown exception into `Warn` + backoff, then runs `PostHandle` — see below.

Tenants are processed sequentially when `PerTenantMaxDegreeOfParallelism == 1` (the default) and via `Parallel.ForEachAsync` otherwise; `-1` means "one per CPU". Each tenant pass gets its own linked `CancellationTokenSource` armed with `PerTenantTimeout`.

## Status acceptance policy

Statuses form an HTTP-like scale, and every decision is made on **two independent axes**: whether a task may be rented at all, and whether the outcome counts as a successful delivery.

### 1. Which statuses are eligible for delivery

`LockAndSelectStatusCodes` in `SqlOutboxBuilder` lists exactly five:

| Code | Status | Meaning |
|---|---|---|
| 0 | `Pending` | Freshly published message |
| 100 | `Processing` | Expired lease — a worker died mid-flight, so the task is picked up again |
| 103 | `Postpone` | Deferred by the consumer |
| 104 | `Retry` | Explicit retry requested by the consumer |
| 400 | `Warn` | Threw, waiting for its backoff to elapse |

Everything ≥ 500 is terminal. So is the whole 2xx range: once a consumer has accepted a message, it is out of the game. On top of the status, a task is only rented when `task_lock_expires_on < @to` (the lease has run out) and `msg_created_at >= @frm` (inside the lookback window).

### 2. Who sets what

| Situation | Status | Set by |
|---|---|---|
| Consumer never touched the message | `Ok` (200) | `PostHandle` — implicit success |
| Consumer called `Ok` / `Created` / `Accepted` / `Ok203` / `NoContent` / `Aborted` | 200–299 | consumer |
| Consumer threw an exception | `Warn` (400) + backoff | `HandleError` |
| Consumer called `Warn(ex, message, postpone)` | 400 + its own delay | consumer |
| `Attempt + 1 > MaxDeliveryAttempts` while the status is `Warn` | `MaximumAttemptsError` (508) | `PostHandle` |
| Consumer called `Error` / `Error501`…`Error507` | 500–507 | consumer |
| Consumer called `Postpone(delay)` / `Retry(delay)` | 103 / 104 + delay | consumer |

### 3. The `PostHandle` rules

Order matters here, and it is load-bearing:

1. **`IsAttemptsError` → 508, checked first.** It requires a warning state (400–499) *and* `Attempt + 1 > MaxDeliveryAttempts`. This check must run **before** the "already handled" short-circuit, otherwise a 400 status would never reach the DLQ and the message would be retried forever.
2. **Status is not `Pending` → leave it alone.** The consumer's decision is final.
3. **Still `Pending` → `Ok()`.** The consumer set no status and threw nothing, so the delivery succeeded implicitly.

`IsAttemptsError` is `Code.IsWarning() && DeliveryInfo.Attempt + 1 > maxDeliveryAttempts`. The `+ 1` is because the attempt counter in the database has not been incremented yet for the failure being processed.

### 4. Consequences worth remembering

- **`MovedPermanently` (301) is not a success range member.** It sits outside `IsSuccess()` (200–299), yet also outside `IsWarning()` and `IsError()`. So it is terminal, is not retried, and is *not* written to the error log. Choosing it is an explicit statement that the message needs a different consumer. It still counts towards the handled-message count.
- **`Postpone` (103) deliberately does not consume an attempt.** `SqlFinishDelivery` increments `delivery_attempt` with `CASE WHEN 103 <> code THEN 1 ELSE 0 END`. This is by design: postponement is fully controlled by the consumer, which decides when — and whether — to give up. `MaxDeliveryAttempts` therefore applies to failures (`Warn`), not to deferrals. Everything else, including a deferral expressed as `Warn`, burns an attempt. If a consumer postpones indefinitely, the dead-lettering decision belongs to that consumer: it must eventually call `Error`/`Error5xx` (terminal) or start failing, at which point `MaxDeliveryAttempts` takes over.
- **The lease is `result.CreatedAt + PostponeDelay`.** On success `PostponeDelay` is zero, but the status is no longer rent-eligible, so it does not matter. For `Warn` with backoff the lease is held for exactly the backoff duration, which is what prevents a second worker from grabbing the same task.
- **Only 5xx reaches the error log.** `GetErrors` filters on `Exception != null && DeliveryResult.Code.IsError()`, so a transient `Warn` no longer writes an `__error$` row or sets `task.error_id`.
- **`Warn` without an explicit delay retries after the batching window, not after a backoff.** `PostponeDelay` defaults to zero, so a consumer that calls `Warn(ex)` and forgets the delay gets no exponential backoff at all — the task simply becomes eligible again once `ToDate = now - BatchingWindow` moves past it. Always pass the delay (or let the courier compute it by throwing).
- **Critical exceptions are not swallowed.** `Deliver` catches with `when (!ex.IsCritical())`. An OOM / StackOverflow escapes before `PostHandle` runs, the lease is left in place until its TTL expires, and the message is picked up again. Repeating is preferable to writing garbage from a dying process.

### 5. What is reported outward

A tenant pass returns a plain `int` — the number of messages the pass handled. There is no "succeeded" counterpart on purpose: `ShouldContinueProcessing` is driven by that count, and a batch whose messages all ended up in `Warn` or the dead letter is still drained work. A success tally would make a fully failed batch indistinguishable from an empty queue and stop the loop. `ProcessMessages` returns the accumulated count, so it reports handled volume, not successful deliveries.

The split is deliberate: a batch where every message failed still has `Processed > 0`, so the greedy loop keeps draining the backlog instead of mistaking a fully-failed batch for an empty queue.

## Empty-queue back-off

The delivery job is timer-driven by `Sa.Schedule`. On every tick it drains the backlog greedily; when nothing is found the wait until the following tick is otherwise a fixed `Interval`. `IEmptyQueueWaitStrategy` lets a consumer group back off while its queue stays empty, instead of polling the database at the base rate forever.

- **`FixedIntervalWaitStrategy`** (default) always returns `Interval` — the pre-existing behavior.
- **`ExponentialIdleWaitStrategy`** (the DI default) grows the wait per consecutive empty poll: after `n` empty polls it returns `min(IdleMaxInterval, Interval * multiplier^(n-1))`, jittered downwards and clamped to `[Interval, IdleMaxInterval]`.

Adaptive back-off is **off by default**: `IdleMaxInterval` defaults to `TimeSpan.Zero`, and both strategies return `Interval` unchanged in that case. Enable it per group with `WithIdleBackoff(maxInterval)` (or disable explicitly with `WithFixedInterval()`); `maxInterval` must be `>= Interval`. Swap the algorithm globally through DI or `IOutboxBuilder.WithIdleWaitStrategy<T>()` (or `WithIdleWaitStrategy(instance)`).

Group state lives in one `OutboxIdleState` per consumer group (`IOutboxIdleStateRegistry`). `DeliveryJob` reports the handled count after each run: any work resets the state to `Interval`, an empty run advances it by one step, and a run while `Paused` resets it so resuming starts from `Interval`. `AdaptiveIdleTiming` (an `IJobTiming`) reads `CurrentInterval` before every iteration. The settings snapshot stays fixed for the cycle, so a runtime `Apply` is honored on the next iteration.

### Configuring the wait policy

A wait policy has two independent parts: the **per-group cap** (`IdleMaxInterval`) that opts a consumer group into back-off, and the **global algorithm** (`IEmptyQueueWaitStrategy`) that computes each step.

**1. Per-group opt-in with the built-in exponential policy.** Short base interval while work is flowing, growth up to a cap while the queue stays empty:

```csharp
services.AddSaOutbox(builder => builder
    .WithDeliveries(d => d
        .AddDelivery<OrderConsumer, OrderPlaced>("orders", (_, cs) => cs
            .WithInterval(TimeSpan.FromSeconds(5))      // poll every 5 s while work is flowing
            .WithIdleBackoff(TimeSpan.FromMinutes(2))   // grow (x2) up to 2 min while empty
        )
    ));
```

Waiting sequence: `5 s → 10 s → 20 s → 40 s → 80 s → 120 s → 120 s …` (each step jittered downwards by up to 20%). The first delivered message resets it to `5 s`.

**2. Keep one group fixed.** Back-off is opt-in, so a group that must stay responsive simply omits `WithIdleBackoff`; `WithFixedInterval()` states that intent explicitly:

```csharp
.AddDelivery<OrderConsumer, OrderPlaced>("orders", (_, cs) => cs
    .WithInterval(TimeSpan.FromSeconds(1))
    .WithFixedInterval());     // always 1 s — equivalent to not opting in
```

**3. Tune the built-in algorithm globally.** Pass an instance to change its parameters (slower growth, more jitter):

```csharp
services.AddSaOutbox(builder => builder
    .WithIdleWaitStrategy(new ExponentialIdleWaitStrategy(multiplier: 1.5, jitter: 0.3))
    .WithDeliveries(/* … groups that call WithIdleBackoff … */));
```

**4. Plug in a custom algorithm.** Implement `IEmptyQueueWaitStrategy` and register it by type; every opted-in group uses it:

```csharp
public sealed class LinearIdleWaitStrategy : IEmptyQueueWaitStrategy
{
    public TimeSpan GetNextWait(TimeSpan baseInterval, TimeSpan maxInterval, int consecutiveEmptyPolls)
    {
        if (maxInterval <= baseInterval) return baseInterval;            // disabled

        var grown = baseInterval * Math.Max(1, consecutiveEmptyPolls);   // linear step
        return grown >= maxInterval ? maxInterval : grown;
    }
}

services.AddSaOutbox(builder => builder
    .WithIdleWaitStrategy<LinearIdleWaitStrategy>()
    .WithDeliveries(/* … */));
```

> Use `WithIdleWaitStrategy<T>()` to let the DI container create the algorithm; use `WithIdleWaitStrategy(instance)` only to pass constructor arguments to a built-in type. With neither call the DI default is `ExponentialIdleWaitStrategy.Shared` — which is inert until a group opts in.

## Consumer settings

`OutboxConsumerSettings` is an immutable `record`; `OutboxConsumerManager` stores and replaces whole snapshots, so a setting can never change under an in-flight delivery.

| Setting | Default | Builder | Notes |
|---|---|---|---|
| `ConsumerGroupId` | — | `WithConsumerGroupId` | Required; also the DI key for the consumer |
| `AsSingleton` | `true` | `AsSingleton` | `true` = one consumer instance for the whole cluster, cached by group id; `false` = a new DI scope per batch |
| `Interval` | 1 min | `WithInterval` | Job periodicity |
| `InitialDelay` | 10 s | `WithInitialDelay` / `StartImmediately` | `AddDeliveryJob` defaults to zero |
| `ConcurrencyLimit` | 1 | `WithConcurrencyLimit` | Group-level schedule concurrency |
| `MaxConcurrency` | 48 | `WithMaxConcurrency` | `AddDeliveryJob` overrides this to 1 |
| `RetryCountOnError` | 0 | `WithRetryCountOnError` / `WithNoRetries` / `WithInfiniteRetries` | Retries of the **job**, not of the message |
| `MaxBatchSize` | 16 | `WithMaxBatchSize` | Upper bound for `IDeliveryBatcher` |
| `MaxProcessingIterations` | 10 | `WithMaxProcessingIterations` / `WithSingleIteration` / `WithUnlimitedIterations` | `-1` = greedy until the queue drains; `AddDeliveryJob` defaults to `-1` |
| `IterationDelay` | zero | `WithIterationDelay` | With zero and `-1` the loop becomes a hot drain loop |
| `LockDuration` | 10 s | `WithLockDuration` / `WithNoLockDuration` | Must be `> 0` |
| `LockRenewal` | 3 s | `WithLockRenewal` | Must satisfy `0 < LockRenewal < LockDuration` |
| `LookbackInterval` | 7 days | `WithLookbackInterval` | Lower bound of the selection window |
| `MaxDeliveryAttempts` | 3 | `WithMaxDeliveryAttempts` | Exhausting it turns `Warn` into 508 |
| `BatchingWindow` | 3 s | `WithBatchingWindow` / `WithNoBatchingWindow` | `ToDate = now - BatchingWindow` — trades latency for batching |
| `PerTenantTimeout` | zero | `WithPerTenantTimeout` | Zero = no timeout |
| `PerTenantMaxDegreeOfParallelism` | 1 | `WithPerTenantMaxDegreeOfParallelism` / `WithTenantSequentialProcessing` / `WithTenantMaxParallelism` | `1` = sequential, `-1` = one per CPU; `0` is rejected |
| `Paused` | `false` | `Paused` / `Resumed` | Checked on every loop iteration |
| `IdleMaxInterval` | zero (disabled) | `WithIdleBackoff(max)` / `WithFixedInterval` | Upper bound of the empty-queue poll back-off; must be `>= Interval` when set |

Note that `AddDeliveryJob` pre-seeds the builder with `StartImmediately()`, `ConcurrencyLimit(1)`, `MaxConcurrency(1)`, `MaxProcessingIterations(-1)` and `PerTenantTimeout(Zero)` **before** invoking your `configure` callback, so those defaults win over `OutboxDefaults`.

### Lock invariants

```csharp
LockDuration  > TimeSpan.Zero
LockRenewal   > TimeSpan.Zero  &&  LockRenewal < LockDuration
```

Both are enforced in `OutboxConsumerSettings.Validate()`, which runs on `TryRegister` and on every `Apply`. A zero `LockDuration` would leave a task in `Processing` forever after a worker crash; a zero `LockRenewal` throws inside `PeriodicTimer`. `WithNoLockDuration()` sets a 50 ms TTL (`OutboxConsumerSettingsBuilder.NoLockDuration`) — effectively no lock, at the cost of possible re-delivery.

## Runtime control

`IOutboxConsumerManager` is the registry the delivery job reads from, and the handle for operational control:

| Member | Behaviour |
|---|---|
| `TryRegister` | Validates the settings, adds them if the group is unknown, returns `false` if already present. Notifies subscribers **outside** the lock |
| `Apply` | Runs the transform **outside** the lock (so a transform may call back into the manager), validates the result, then swaps it in if the snapshot it transformed is still current; retries the transform otherwise. Throws if the group is not registered |
| `Get` / `IsRegistered` / `IsPaused` | Lock-protected reads |
| `Pause` / `Resume` | `Apply(group, s => s with { Paused = ... })` — takes effect on the next loop iteration |
| `Unregister` | Drops settings and listeners |
| `GetAllConsumerGroupIds` | Returns a **copy**, not a live view — the lock is released before the caller enumerates, so handing out the live key collection would expose concurrent mutations and throw |
| `Subscribe` | Returns an `IDisposable` handle. Listeners are invoked over a snapshot taken under the lock; a throwing subscriber is logged through `ILogger` and does not stop the others |

Because settings are replaced atomically, a runtime `Apply` is observed by the next iteration and never mid-batch.

## DI registration

`Setup.AddOutboxDelivery` registers, all via `TryAdd*` so any of them can be overridden before the call:

```
FilterFactory (singleton)
IOutboxContextFactory      → OutboxContextFactory
IDeliveryBatcher           → DeliveryBatcher
IDeliveryProcessor         → DeliveryProcessor
IDeliveryCourier           → DeliveryCourier
IDeliveryTenant            → DeliveryTenant
IDeliveryLifetimeInvoker   → DeliveryLifetimeInvoker
IDeliverySnapshot          → DeliverySnapshot
IOutboxConsumerManager     → OutboxConsumerManager
IEmptyQueueWaitStrategy    → ExponentialIdleWaitStrategy
IOutboxIdleStateRegistry   → OutboxIdleStateRegistry
```

It also calls `AddOutboxPartitional()` (tenant support) and `AddMessagesMetadata()`, then invokes your `IDeliveryBuilder` callback.
