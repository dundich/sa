# SaWorkQueue — Async Queue with Concurrency Limiting

> High-performance task queue for .NET 10 with dynamic scaling, DI integration, and a type-safe API.

---

## Features

| Feature | Description |
|---------|-------------|
| **Concurrency limiting** | Control the number of simultaneously executing tasks via `ConcurrencyLimit` |
| **Dynamic scaling** | Change the limit at runtime: `queue.ConcurrencyLimit = newLimit` |
| **Scaling strategies** | `Lifo` • `Fifo` • `RoundRobin` • `Random` — choose the one that fits your scenario |
| **DI integration** | Registration via `AddSaWorkQueue<TProcessor, TInput>` with configuration support |
| **Zero-allocation logging** | `[LoggerMessage]` generation for `ILogger` |
| **Safe shutdown** | `DisposeAsync`, `ShutdownAsync`, `WaitForIdleAsync` — idempotent and thread‑safe |
| **Fault tolerance** | Configurable error strategy: `ShutdownQueue` (default), `StopReader`, or `Continue` |

---

## 🚀 Quick Start

### 1️⃣ Implement your task processor

```csharp
public sealed class OrderWork(ILogger<OrderWork> logger) : ISaWork<OrderInput>
{
    public async Task Execute(OrderInput input, CancellationToken ct)
    {
        logger.LogInformation("Processing order {OrderId}", input.OrderId);
        await ProcessAsync(input, ct); // Your business logic
    }
}
```

### 2️⃣ Register with DI

```csharp
builder.Services.AddSaWorkQueue<OrderWork, OrderInput>((sp, opts) =>
    opts
        .WithConcurrencyLimit(4)
        .WithQueueCapacity(100)
        .WithReaderScalingStrategy(SaReaderScalingStrategy.RoundRobin)
        .WithStatusChanged((input, status, ex) =>
        {
            // logger.LogDebug("Order {Id} → {Status}", input.OrderId, status);
        }));
```

### 3️⃣ Use via injection

```csharp
public class OrderService(ISaWorkQueue<OrderInput> queue)
{
    public async Task SubmitAsync(OrderInput order, CancellationToken ct)
    {
        await queue.Enqueue(order, ct); // Does not block the caller
    }

    public bool IsIdle() => queue.IsIdle();
    public int Pending => queue.QueueTasks;
}
```

---

## ⚙️ `SaWorkQueueOptions<TInput>` Configuration

```csharp
SaWorkQueueOptions<TInput>.Create(processor)
    .WithConcurrencyLimit(int)        // Concurrency limit (default: CPU count)
    .WithQueueCapacity(int)           // Queue capacity (default: equals the limit)
    .WithMaxConcurrency(int)          // Absolute maximum number of readers
    .WithReaderScalingStrategy(enum)  // Lifo | Fifo | RoundRobin | Random
    .WithSingleWriter(bool)           // Optimisation for a single write source
    .WithFullMode(enum)               // Wait | DropWrite | DropOldest
    .WithStatusCallback(Action<...>)  // Callback on task status change
    .WithHandleItemFaulted(Func<...>) // Continue | StopReader | ShutdownQueue
    .WithItemDisplayName(Func<...>)   // Display name for each work item (e.g., for logging)
```

---

## Reader Scaling Strategies

| Strategy | Behaviour | Best for |
|----------|-----------|----------|
| `Lifo` (default) | Cancels the most recently created | CPU‑bound tasks, cache locality |
| `Fifo` | Cancels the earliest created | Connection pools, databases, rotation |
| `RoundRobin` 🔄 | Cycles through the queue | Load balancing, stable workers |
| `Random` 🎲 | Random selection | Testing, avoiding patterns |

---

## 🔑 Key Methods of `ISaWorkQueue<TInput>`

```csharp
// Enqueue a task (does not block if there is room in the queue)
await queue.Enqueue(input, cancellationToken);

// Wait until all tasks have been fully processed
await queue.WaitForIdleAsync(ct);

// Graceful shutdown: finish active tasks + clear the queue
await queue.ShutdownAsync();

// Emergency stop of readers (without waiting for completion)
queue.ForceCancelReaders();

// Monitoring
bool idle = queue.IsIdle();      // true if no active or pending tasks
int pending = queue.QueueTasks;  // total tasks in progress + in the queue
int limit = queue.ConcurrencyLimit; // current concurrency limit
```

---

## ⚠️ Important Notes

1. **Lifecycle**: the queue is registered as `Singleton`. Do not use `Scoped`/`Transient`.
2. **`StatusChanged`**: invoked on a thread‑pool thread. Avoid long synchronous operations inside.
3. **Cancellation**: tasks receive a `CancellationToken`. Handle `OperationCanceledException` properly.
4. **Default error strategy**: `Shutdown` — a faulted item shut down the queue. Override with `.WithHandleItemFaulted()` if you need different behavior.
5. **`ForceCancelReaders` / `ForceCancelReadersAsync`**: emergency stop — kills reader tasks immediately. After calling, restore concurrency by setting `ConcurrencyLimit = X` to spawn replacement readers.
6. **Thread safety**: all public methods are thread-safe. Changing `ConcurrencyLimit` at runtime adjusts reader count without losing queued items.
7. **Reusability**: all shutdown/cleanup methods (`Shutdown`, `ShutdownAsync`, `Dispose`, `DisposeAsync`) are idempotent — safe to call multiple times.
8. **`ConcurrencyLimit = 0`**: pauses all processing (kills all readers). Set back to a positive value to resume.

