# Sa.Utils.WorkQueue

High-performance async task queue for .NET with bounded capacity, dynamic concurrency scaling, and a type-safe API.

---

## Features

| Feature | Description |
|---------|-------------|
| **Concurrency limiting** | Control the number of simultaneously executing tasks via `ConcurrencyLimit` |
| **Dynamic scaling** | Change the limit at runtime: `queue.ConcurrencyLimit = newLimit` |
| **Scaling strategies** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — choose the one that fits your scenario |
| **DI integration** | Registration via `AddSaWorkQueue<TProcessor, TInput>` or delegate-based `AddSaWorkQueue<TInput>` |
| **Zero-allocation logging** | `[LoggerMessage]` source generator for `ILogger` |
| **Safe shutdown** | `ShutdownAsync`, `DisposeAsync` — idempotent and thread-safe |
| **Error strategies** | Per-item fault handling: `Continue`, `StopReader`, or `ShutdownQueue` |
| **Status callbacks** | Track item lifecycle: `Running` → `Completed` / `Faulted` / `Cancelled` / `Aborted` |
| **Back-pressure** | `BoundedChannel` — writers block (`Wait`) while the queue is full |

---

# 📄 Architectural Description: `SaWorkQueue<TInput>`

## 1. General Purpose

`SaWorkQueue<TInput>` is a high-performance, thread-safe background task processing queue built on the **Producer-Consumer** pattern using `System.Threading.Channels`. The class is designed for async task execution (`ISaWork<TInput>`) with dynamic management of the worker thread pool (readers), error handling, and idle-state tracking.

## 2. Key Architectural Principles

### A. Dynamic Pool Capacity (`ConcurrencyLimit`)
* **Semantics:** The `ConcurrencyLimit` property reflects the **current actual pool capacity** (the number of physically existing and ready-to-work readers), not just a static "target setting."
* **Behavior on failure:** If a reader terminates (e.g., due to `SaExecutionErrorStrategy.StopReader` or a `ForceCancelReaders` call), the `ConcurrencyLimit` value **decreases** atomically. This guarantees the property always reflects the real number of available worker units.
* **Recovery:** If a reader has terminated (strategy `StopReader`, `ForceCancelReaders`, crash), the pool capacity decreases. To recover, set `ConcurrencyLimit` to the target value — the setter will spawn the missing readers.

### B. Precise Task Accounting (`_taskCount` and `IsIdle`)
* **Single source of truth:** The `IsIdle()` method relies **exclusively** on the value `volatile int _taskCount == 0`.
* **Why:** Checking `_queue.Reader.Count` is subject to race conditions between the moment a task is dequeued from the channel and the moment its execution begins. The `_taskCount` counter is incremented in `MarkActive()` *before* writing to the channel and decremented in `MarkInactive()` *after* processing completes, providing a 100% accurate idle signal via `TaskCompletionSource` (`_idleTcs`).
* **Negative-value protection:** `MarkInactive()` contains a `if (_taskCount > 0)` check to prevent the counter from going negative during a race between `Enqueue` and `Shutdown`.

### C. Deterministic Forced Stop (`ForceCancelReaders`)
* The `ForceCancelReaders` and `ForceCancelReadersAsync` methods don't just send a cancellation signal (`Cancel()`), but **guaranteedly wait** for the physical completion of reader tasks (`Task.WaitAll` / `Task.WhenAll`).
* This is critical: only after this wait is it guaranteed that `_readerCount` and `_concurrency` have been correctly updated by `RemoveReader`, and subsequent operations (e.g., changing `ConcurrencyLimit`) work with the current state.

## 3. Main Components

| Component | Purpose |
| :--- | :--- |
| `Channel<WorkItem> _queue` | Thread-safe buffer for passing tasks from producers to consumers. Configured with `SingleReader = false` (multiple consumers). |
| `_readerCount` / `_taskReaders` | Track the actual number of started processing loops (`ReaderLoopAsync`). |
| `_concurrency` | Volatile field storing the current pool capacity. Synced with `_readerCount` during normal operation, managed under `_readersSync` on abnormal termination. |
| `_taskCount` / `_idleTcs` | Efficient idle-wait mechanism without active polling. |
| `_state` (`QueueState`) | State machine: `Active` (0), `Shutdown` (1), `Disposed` (2). Managed via `Interlocked.CompareExchange`. |

## 4. Error Handling and Strategies (`SaExecutionErrorStrategy`)

On exception in `ISaWork<TInput>.Execute`, the `_handleItemFaulted` delegate fires, returning one of:
1. **`Continue`**: The item is marked as `Faulted`, but the reader continues pulling the next tasks from the channel.
2. **`StopReader`**: The item is marked as `Faulted`, the current reader terminates its loop (`return false`), triggering `RemoveReader`. **Pool capacity (`ConcurrencyLimit`) decreases by 1.** Other readers continue working.
3. **`ShutdownQueue`**: Asynchronous termination of the entire queue is initiated (`ShutdownAsync`), all new tasks will be rejected.

## 5. Lifecycle and Shutdown

* **Graceful Shutdown (`Shutdown` / `ShutdownAsync`)**:
  1. Transitions state to `Shutdown`.
  2. Cancels `_shutdownCts` (signal to readers to finish after their current task).
  3. Closes the channel for writing (`TryComplete()`).
  4. Waits for all reader tasks to complete (`WaitForReadersToCompleteAsync`).
  5. Clears remaining items in the channel (`ClearRemainingItems`), marking them as `Faulted` and resetting `_taskCount` to 0 for correct `IsIdle()`.
* **Dispose**: Guarantees `Shutdown` is called and `_shutdownCts` is released. Repeated calls are safe (idempotent).

## 6. 🤖 Critical Rules for AI Agents (⚠️ IMPORTANT)

These rules MUST be followed when modifying this code:

1. **NEVER** use direct assignment `_concurrency = _readerCount`. Pool capacity change on reader removal is done via `_concurrency--` inside `RemoveReader` (under `_readersSync`) to avoid race conditions.
2. **NEVER** rely on `_queue.Reader.Count` to determine `IsIdle()`. Use only `_taskCount == 0`.
3. When adding new forced-interruption methods, always include a wait for task completion (`Task.WhenAll`) so that counter state has time to synchronize.
4. The `ConcurrencyLimit` setter computes delta from the actual live reader count (`_readerCount - _pendingRemovals`), not from `_readerCount` — this prevents spurious spawns/cancels for already-cancelled readers.
5. `RemoveReader` is called in the `finally` of `ReaderLoopAsync`, which executes **after** `MarkInactive()` (in `ExecuteItemAsync`'s finally). This means `WaitForIdleAsync` may return before `RemoveReader` completes. Do not assume `_concurrency` is fully updated immediately after `WaitForIdleAsync` returns.
6. All reader-list mutations (`_ctsReaders`, `_taskReaders`, `_pendingRemovals`, `_intentionalRemovals`, `_forceCancelled`) must happen under `lock (_readersSync)`.
7. All task-count mutations (`_taskCount`, `_idleTcs`) must happen under `lock (_wiSync)`.

---

## 🚀 Quick Start

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
        .WithReaderScalingStrategy(SaReaderScalingStrategy.RoundRobin)
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

## ⚙️ `SaWorkQueueOptions<TInput>` Configuration

All parameters are immutable record fields with fluent `With*` methods:

```csharp
SaWorkQueueOptions<TInput>.Create(processor)
    .WithConcurrencyLimit(int)                    // Concurrency limit (default: CPU count)
    .WithQueueCapacity(int)                       // Channel capacity (default: equals limit)
    .WithMaxConcurrency(int)                      // Absolute ceiling of readers (default: CPU count)
    .WithSingleWriter(bool)                       // Optimisation for single-writer scenarios
    .WithReaderScalingStrategy(enum)              // Lifo | Fifo | RoundRobin | Random
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

## Reader Scaling Strategies

Applied when decreasing `ConcurrencyLimit` at runtime — determines which readers to cancel:

| Strategy | Behaviour | Best for |
|----------|-----------|----------|
| `Lifo` | Cancels the most recently created readers | CPU-bound tasks, cache locality |
| `Fifo` | Cancels the oldest readers | Resource rotation, even lifetime distribution |
| `RoundRobin` | Cyclic reader cancellation | Stable workers, balanced load |
| `Random` | Random reader cancellation | Testing, avoiding patterns |

---

## 🔑 `ISaWorkQueue<TInput>` API

| Member | Kind | Description |
|--------|------|-------------|
| `Enqueue(input, ct)` | Method | Add a task (non-blocking if there is room) |
| `WaitForIdleAsync(ct)` | Method | Wait until all tasks complete |
| `ShutdownAsync()` | Method | Graceful shutdown (finish active + drain) |
| `Shutdown()` | Method | Synchronous shutdown |
| `ForceCancelReaders()` | Method | Emergency stop of all readers (bounded wait) |
| `ForceCancelReadersAsync(timeout, ct)` | Method | Async emergency stop with optional timeout |
| `IsIdle()` | Property | `true` if no pending/active tasks |
| `IsEnabled` | Property | `true` while queue is active |
| `QueueTasks` | Property | Total tasks in progress + queued |
| `ConcurrencyLimit` | Property | Current parallelism limit (mutable) |
| `MaxConcurrency` | Property | Absolute ceiling |
| `QueueCapacity` | Property | Bounded channel capacity |
| `ShutdownError` | Property | Exception that triggered shutdown, if any |

---

## Status Lifecycle

Each item flows through statuses communicated via the `StatusChanged` callback:

| Status | Meaning |
|--------|---------|
| `Running` | Item is being processed |
| `Completed` | Finished successfully |
| `Faulted` | Unhandled error occurred |
| `Cancelled` | Cancelled by system (shutdown, timeout) |
| `Aborted` | Cancelled explicitly by caller's token |

---

## Error Strategies

Configured via `.WithHandleItemFaulted(...)`:

| Strategy | Behaviour |
|----------|----------|
| `Continue` | Mark item as Faulted, continue processing remaining items |
| `StopReader` | Mark item as Faulted, stop current reader (capacity decreases; restore via `ConcurrencyLimit = X`) |
| `ShutdownQueue` | Mark item as Faulted, trigger full queue shutdown |

Default: `ShutdownQueue` — an item fault triggers a shutdown. For fault-tolerant pipelines, override to `Continue` or `StopReader`.

---

## ⚠️ Important Notes

1. **Lifecycle**: registered as `Singleton`. Do not use `Scoped`/`Transient`.
2. **`StatusChanged` callback**: invoked synchronously a thread-pool thread. Avoid long-running operations inside. Handler exceptions are logged but not propagated.
3. **Cancellation**: each `Enqueue` accepts a `CancellationToken`. Items distinguish caller-initiated cancellation (`Aborted`) from system cancellation (`Cancelled`).
4. **Thread safety**: all public members are thread-safe. Changing `ConcurrencyLimit` at runtime adjusts reader count without losing queued items.
5. **Idempotent shutdown**: `ShutdownAsync`, `Shutdown`, `Dispose`, `DisposeAsync` are safe to call multiple times.
6. **`ConcurrencyLimit = 0`**: pauses all processing (kills all readers). Restore a positive value to resume.
7. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: emergency stop — immediately cancels all reader tasks. The sync variant waits up to `ShutdownTimeout` (default 30s) for readers to terminate; the async variant accepts an optional `TimeSpan? timeout`. After calling, restore concurrency by setting `ConcurrencyLimit = X` to spawn replacement readers.
8. **Delegate-based registration**: `AddSaWorkQueue<TInput>(configureOptions)` accepts a factory returning `SaWorkQueueOptions<TInput>`, allowing registration without an `ISaWork<TInput>` class.
