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
| **Back-pressure** | Configurable full-channel behavior: `Wait`, `DropOldest`, `DropNewest`, `DropWrite` |

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
        .WithFullMode(BoundedChannelFullMode.DropOldestWhenFull)
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
    .WithFullMode(BoundedChannelFullMode)          // Wait | DropOldest | DropNewest | DropWrite
    .WithReaderScalingStrategy(enum)              // Lifo | Fifo | RoundRobin | Random
    .WithStatusCallback(Action<TInput, SaWorkStatus, Exception?>)
    .WithHandleItemFaulted(Func<TInput, Exception, SaExecutionErrorStrategy>)
    .WithItemDisplayName(Func<TInput, string>)    // Custom display name for logging
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
| `ForceCancelReaders()` | Method | Emergency stop of all readers |
| `ForceCancelReadersAsync()` | Method | Async emergency stop of all readers |
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
| `StopReader` | Mark item as Faulted, stop current reader (auto-replaced) |
| `ShutdownQueue` | Mark item as Faulted, trigger full queue shutdown |

Default: `ShutdownQueue` — an item fault triggers a shutdown. For fault-tolerant pipelines, override to `Continue` or `StopReader`.

---

## Back-Pressure Modes

Configured via `.WithFullMode(...)`:

| Mode | Behaviour |
|------|----------|
| `Wait` | Block the caller until space is available |
| `DropOldest` | Remove the oldest queued item, accept the new one |
| `DropNewest` | Discard the incoming item |
| `DropWrite` | Fail the enqueue call immediately |

---

## ⚠️ Important Notes

1. **Lifecycle**: registered as `Singleton`. Do not use `Scoped`/`Transient`.
2. **`StatusChanged` callback**: invoked synchronously on a thread-pool thread. Avoid long-running operations inside. Handler exceptions are logged but not propagated.
3. **Cancellation**: each `Enqueue` accepts a `CancellationToken`. Items distinguish caller-initiated cancellation (`Aborted`) from system cancellation (`Cancelled`).
4. **Thread safety**: all public members are thread-safe. Changing `ConcurrencyLimit` at runtime adjusts reader count without losing queued items.
5. **Idempotent shutdown**: `ShutdownAsync`, `Shutdown`, `Dispose`, `DisposeAsync` are safe to call multiple times.
6. **`ConcurrencyLimit = 0`**: pauses all processing (kills all readers). Restore a positive value to resume.
7. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: emergency stop — immediately cancels all reader tasks. After calling, restore concurrency by setting `ConcurrencyLimit = X` to spawn replacement readers.
8. **Delegate-based registration**: `AddSaWorkQueue<TInput>(configureOptions)` accepts a factory returning `SaWorkQueueOptions<TInput>`, allowing registration without an `ISaWork<TInput>` class.
