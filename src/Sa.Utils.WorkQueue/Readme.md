# Sa.Utils.WorkQueue

High-performance async task queue for .NET with bounded capacity, dynamic concurrency scaling, and a type-safe API.

---

## Features

| Feature | Description | Details |
|---------|-------------|---------|
| **Concurrency limiting** | Control the number of simultaneously executing tasks via `ConcurrencyLimit` | [Concurrency & Scaling](#concurrency--scaling) |
| **Dynamic scaling** | Change the limit at runtime: `queue.ConcurrencyLimit = newLimit` | [Concurrency & Scaling](#concurrency--scaling) |
| **Back-pressure** | `BoundedChannel` — `Enqueue` strategy when the buffer is full: `Wait` (default, blocks) • `Skip` (returns `false`) • `Throw` (`SaWorkQueueFullException`); plus non-blocking `TryEnqueue` and batch `EnqueueMany` | [Enqueue Strategies](#enqueue-strategies) |
| **Reader cancellation order** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — choose which readers are cancelled when the limit is decreased | [Reader Cancellation Order](#reader-cancellation-order) |
| **Cancel modes** | `Hard` (default) or `Soft` — how in-flight work is treated when readers are removed at runtime | [Reader Cancel Modes](#reader-cancel-modes) |
| **DI integration** | Registration via `AddSaWorkQueue<TProcessor, TInput>` or delegate-based `AddSaWorkQueue<TInput>` | [Quick Start](#quick-start) |
| **Safe shutdown** | `ShutdownAsync`, `DisposeAsync` — idempotent and thread-safe | [Important Notes](#-important-notes) |
| **Error strategies** | Per-item fault handling: `Continue`, `StopReader`, or `ShutdownQueue` | [Error Strategies](#error-strategies) |
| **Status callbacks** | Track item lifecycle: `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` / `Skipped` | [Status Lifecycle](#status-lifecycle) |

---

## Quick Start

### 1️⃣ Implement your task processor

```csharp
public sealed class OrderWork(ILogger<OrderWork> logger) : ISaWork<OrderInput>
{
    public async Task Execute(OrderInput input, CancellationToken ct)
    {
        logger.LogInformation("Processing order {OrderId}", input.OrderId);
        await ProcessOrderAsync(input, ct); // Your business logic
    }
}
```

### 2️⃣ Register with DI

```csharp
builder.Services.AddSaWorkQueue<OrderWork, OrderInput>((sp, opts) =>
    opts
        .WithConcurrencyLimit(4)
        .WithQueueCapacity(100)
        .WithMaxConcurrency(16)
        .WithReaderCancellationOrder(SaReaderCancellationOrder.RoundRobin)
        .WithStatusCallback((input, status, ex) =>
        {
            // logger.LogDebug("Order {Id} → {Status}", input.OrderId, status);
        }));
```

### 3️⃣ Use via injection

```csharp
public class OrderService(ISaWorkQueue<OrderInput> queue)
{
    public async Task SubmitAsync(OrderInput order, CancellationToken ct)
        => await queue.Enqueue(order, ct);

    public async Task WaitForCompletionAsync(CancellationToken ct)
        => await queue.WaitForIdleAsync(ct);

    public bool IsIdle() => queue.IsIdle();
    public int Pending => queue.QueueTasks;
}
```

---

## Configuration

All parameters are immutable record fields with fluent `With*` methods on `SaWorkQueueOptions<TInput>`:

```csharp
SaWorkQueueOptions<TInput>.Create(processor)
    .WithConcurrencyLimit(int)                    // Concurrency limit (default: CPU count)
    .WithQueueCapacity(int)                       // Channel capacity (default: equals MaxConcurrency)
    .WithMaxConcurrency(int)                      // Absolute ceiling of readers (default: CPU count)
    .WithSingleWriter(bool)                       // Optimisation for single-writer scenarios
    .WithReaderCancellationOrder(enum)             // Lifo | Fifo | RoundRobin | Random
    .WithReaderCancelMode(enum)                   // Hard | Soft — in-flight work on reader removal
    .WithEnqueueStrategy(enum)                    // Wait | Skip | Throw — behaviour when the buffer is full
    .WithStatusCallback(Action<TInput, SaWorkStatus, Exception?>)
    .WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy>)
    .WithItemDisplayName(Func<TInput, string>)    // Custom display name for logging
    .WithShutdownTimeout(TimeSpan)                 // Max wait for readers on shutdown/force-cancel (default: 30s)
```

### Creating options

```csharp
// Via ISaWork<TInput> implementation
var opts = SaWorkQueueOptions<OrderInput>.Create(new OrderWork(logger));

// Via delegate (no class needed)
var opts = SaWorkQueueOptions<OrderInput>.Create(async (input, ct) => {
    await ProcessAsync(input, ct);
});
```

---

## Concurrency & Scaling

The queue runs up to `ConcurrencyLimit` readers concurrently (default: CPU count), never exceeding `MaxConcurrency`. The limit is mutable at runtime — readers are spawned or cancelled as needed, and queued items are not affected.

```csharp
// Configure
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithConcurrencyLimit(4)      // readers to start with
    .WithMaxConcurrency(16);      // ceiling for runtime scaling
    .WithQueueCapacity(100);      // bounded buffer size
```

```csharp
// Scale at runtime
queue.ConcurrencyLimit = 8;  // spawn more readers
queue.ConcurrencyLimit = 4;  // cancel readers (per the configured cancellation order)
queue.ConcurrencyLimit = 0;  // pause processing; set a positive value to resume
```

When a reader terminates on its own (error strategy `StopReader`, `ForceCancelReaders`), `ConcurrencyLimit` decreases accordingly — set it back to the target value to restore the pool.

---

## Reader Cancellation Order

Determines which readers to cancel when `ConcurrencyLimit` is decreased at runtime (default: `Lifo`).

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithReaderCancellationOrder(SaReaderCancellationOrder.RoundRobin);
```

| Strategy | Behaviour | Best for |
|----------|-----------|----------|
| `Lifo` (default) | Cancels the most recently created readers | CPU-bound tasks, cache locality |
| `Fifo` | Cancels the oldest readers | Resource rotation, even lifetime distribution |
| `RoundRobin` | Cyclic reader cancellation | Stable workers, balanced load |
| `Random` | Random reader cancellation | Testing, avoiding patterns |

---

## Reader Cancel Modes

Controls how in-flight work is treated when readers are removed at runtime (limit decrease, or pause via `ConcurrencyLimit = 0`).

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithReaderCancelMode(SaReaderCancelMode.Soft);
```

| Mode | Behaviour | Best for |
|------|-----------|----------|
| `Hard` (default) | The in-flight item is cancelled immediately (`Cancelled`) and dropped | Fast stop, work that is cheap to interrupt |
| `Soft` | The removed reader finishes its current item (`Completed`) before exiting; items still in the channel stay queued | Expensive-to-interrupt work (I/O, third-party calls, long computations) |

`ForceCancelReaders` / `ForceCancelReadersAsync` and `Shutdown` / `ShutdownAsync` **always** interrupt in-flight work regardless of the mode.

---

## Enqueue Strategies

`Enqueue` returns `ValueTask<bool>` and behaves according to the configured `SaEnqueueStrategy` when the bounded buffer is full:

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithEnqueueStrategy(SaEnqueueStrategy.Wait);   // Wait | Skip | Throw
```

| Strategy | Behaviour when the buffer is full | Best for |
|----------|-----------------------------------|----------|
| `Wait` (default) | Blocks until space is available, then returns `true` | Producers that should apply back-pressure |
| `Skip` | Drops the item and returns `false` (not counted in `QueueTasks`; reported as `Skipped`) | Fire-and-forget producers: metrics, telemetry, logs |
| `Throw` | Throws `SaWorkQueueFullException` (carries `QueueCapacity`, `QueuedCount`, and the item's display name) | Producers that must react explicitly to overload |

```csharp
var accepted = await queue.Enqueue(input, ct);
if (!accepted)
{
    // Skip mode: the buffer was full, the item was dropped (reported as Skipped)
}

// Free slots before the buffer is full (informational)
int free = queue.AvailableCapacity;
```

A **stopped or disposed** queue always throws (`InvalidOperationException` / `ObjectDisposedException`) regardless of the strategy. Since `Enqueue` is async, the exception is carried by the returned `ValueTask` — observe it with `await`.

`TryEnqueue(input)` is a non-blocking variant that never honors the strategy: it performs a single try-write — `true` when the item is accepted, `false` when the buffer is full (the item is dropped, not counted, and reported as `Skipped`). Use it on hot paths where the caller cannot block or `await`. A stopped or disposed queue still throws — synchronously this time.

`EnqueueMany(inputs, ct)` adds several items in a single call and returns the number accepted (`ValueTask<int>`). Each item honors the configured strategy: `Wait` blocks until space is available, so all items are eventually accepted; `Skip` drops the items that no longer fit, reporting each of them as `Skipped`; `Throw` throws `SaWorkQueueFullException` as soon as the buffer becomes full, carrying `AcceptedCount` and `TotalCount`. Items accepted before a `Throw` failure remain in the queue.

---

## Error Strategies

Controls what happens when `ISaWork<TInput>.Execute` throws.

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithHandleItemFaulted((input, ex) =>
        ex is OutOfMemoryException
            ? SaExecutionErrorStrategy.ShutdownQueue
            : SaExecutionErrorStrategy.Continue);
```

| Strategy | Behaviour |
|----------|----------|
| `Continue` | Mark item as `Faulted`, continue processing remaining items |
| `StopReader` | Mark item as `Faulted`, stop current reader (capacity decreases; restore via `ConcurrencyLimit = X`) |
| `ShutdownQueue` (default) | Mark item as `Faulted`, trigger full queue shutdown |

For fault-tolerant pipelines, override the default to `Continue` or `StopReader`.

---

## Status Lifecycle

Each item flows through statuses communicated via the `StatusChanged` callback:

```csharp
var opts = SaWorkQueueOptions<OrderInput>.Create(processor)
    .WithStatusCallback((input, status, ex) =>
    {
        // e.g. logger.LogDebug("Order {Id} → {Status}", input.OrderId, status);
    });
```

| Status | Meaning |
|--------|---------|
| `Running` | Item is being processed |
| `Completed` | Finished successfully |
| `Faulted` | Unhandled error occurred |
| `Cancelled` | Cancelled by system (shutdown, timeout) |
| `Aborted` | Cancelled explicitly by caller's token |
| `Skipped` | Not accepted because the buffer was full (`Skip` strategy, `TryEnqueue`, or `EnqueueMany`); the item was never processed |

---

## API — `ISaWorkQueue<TInput>`

| Member | Kind | Description |
|--------|------|-------------|
| `Enqueue(input, ct)` | Method | Add a task; returns `false` only in `Skip` mode when the buffer is full |
| `TryEnqueue(input)` | Method | Non-blocking enqueue; returns `false` when the buffer is full (strategy-independent) |
| `EnqueueMany(inputs, ct)` | Method | Batch enqueue; returns the number of items accepted (each item honors the configured strategy) |
| `WaitForIdleAsync(ct)` | Method | Wait until all tasks complete (returns immediately if paused, `ConcurrencyLimit = 0`) |
| `ShutdownAsync()` | Method | Cancellation shutdown: cancels all readers (in-flight work is interrupted, not finished), waits for readers bounded by `ShutdownTimeout`, drains the rest of the buffer as `Faulted` |
| `Shutdown()` | Method | Synchronous shutdown (blocks the calling thread) |
| `ForceCancelReaders()` | Method | Emergency stop of all readers (bounded wait) |
| `ForceCancelReadersAsync(timeout, ct)` | Method | Async emergency stop with optional timeout |
| `IsIdle()` | Method | `true` if no pending/active tasks |
| `IsEnabled` | Property | `true` while queue is active |
| `QueueTasks` | Property | Total tasks in progress + queued |
| `ConcurrencyLimit` | Property | Current parallelism limit (mutable) |
| `MaxConcurrency` | Property | Absolute ceiling |
| `QueueCapacity` | Property | Bounded channel capacity |
| `AvailableCapacity` | Property | Free slots in the buffer (informational) |
| `ShutdownError` | Property | Exception that triggered shutdown, if any |

---

## ⚠️ Important Notes

1. **Lifecycle**: registered as `Singleton`. Do not use `Scoped`/`Transient`.
2. **`StatusChanged` callback**: invoked synchronously on a thread-pool thread. Avoid long-running operations inside. Handler exceptions are logged but not propagated.
3. **Cancellation**: each `Enqueue` accepts a `CancellationToken`. Items distinguish caller-initiated cancellation (`Aborted`) from system cancellation (`Cancelled`).
4. **Thread safety**: all public members are thread-safe. Changing `ConcurrencyLimit` at runtime adjusts reader count without losing queued items.
5. **Idempotent shutdown**: `ShutdownAsync`, `Shutdown`, `Dispose`, `DisposeAsync` are safe to call multiple times.
6. **`ConcurrencyLimit = 0`**: pauses all processing (cancels all readers; in `Soft` mode the in-flight items are allowed to finish first). Restore a positive value to resume.
7. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: emergency stop — immediately cancels all reader tasks. The sync variant waits up to `ShutdownTimeout` (default 30s) for readers to terminate; the async variant accepts an optional `TimeSpan? timeout`. After calling, restore concurrency by setting `ConcurrencyLimit = X` to spawn replacement readers.
8. **Delegate-based registration**: `AddSaWorkQueue<TInput>(configureOptions)` accepts a factory returning `SaWorkQueueOptions<TInput>`, allowing registration without an `ISaWork<TInput>` class.
9. **`Enqueue` return value**: `ValueTask<bool>` — `false` only in `Skip` mode with a full buffer (the item is dropped and reported as `Skipped` via `StatusChanged`). A stopped/disposed queue always throws; the exception is carried by the `ValueTask` and surfaced by `await`.
10. **`AvailableCapacity`**: informational only — free slots in the buffer. It does not affect `IsIdle()`.

---

## 🤖 Critical Rules for AI Agents (⚠️ IMPORTANT)

These rules MUST be followed when modifying this code:

1. **NEVER** use direct assignment `_concurrency = _ctsReaders.Count`. Pool capacity change on reader removal is done via `_concurrency--` inside `RemoveReader` (under `_readersSync`) to avoid race conditions.
2. **NEVER** rely on `_queue.Reader.Count` to determine `IsIdle()`. Use only `_taskCount == 0`.
3. When adding new forced-interruption methods, always include a wait for task completion (`Task.WhenAll`) so that counter state has time to synchronize.
4. The `ConcurrencyLimit` setter computes delta from the actual live reader count (`_ctsReaders.Count - _pendingRemovals`), not from the configured `_concurrency` — this prevents spurious spawns/cancels for already-cancelled readers.
5. `RemoveReader` is called in the `finally` of `ReaderLoopAsync`, which executes **after** `MarkInactive()` (in `ExecuteItemAsync`'s finally). This means `WaitForIdleAsync` may return before `RemoveReader` completes. Do not assume `_concurrency` is fully updated immediately after `WaitForIdleAsync` returns.
6. All reader-list mutations (`_ctsReaders`, `_ctsWorks`, `_taskReaders`, `_pendingRemovals`, `_intentionalRemovals`, `_forceCancelled`) must happen under `lock (_readersSync)`. The three parallel lists are removed at the same index in `RemoveReader` — never desynchronise them.
7. All task-count mutations (`_taskCount`, `_idleTcs`) must happen under `lock (_wiSync)`.
