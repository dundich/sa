# Sa.Schedule

A .NET task scheduler — periodic and one-shot jobs, dynamic concurrency control, error recovery strategies, interceptors, and graceful shutdown.

---

## Features

- **[Periodic & one-shot jobs](#defining-jobs)** — `EveryMinutes`, `EveryHours`, `RunOnce`, etc.
- **[Cron scheduling](#cron-scheduling)** — standard 5-field expressions.
- **[Dynamic concurrency](#concurrency-model)** — change `ConcurrencyLimit` on the fly.
- **[Error recovery policies](#error-handling)** — retry, suppress errors, stop individual job or the whole app.
- **[Interceptors](#interceptors)** — chain-of-responsibility for logging, metrics, tracing.
- **[Lambda jobs](#lambda-jobs)** — anonymous delegates without dedicated classes.
- **[Graceful shutdown](#runtime-management)** — configurable timeout for running iterations.
- **[Scoped services](#defining-jobs)** — DbContext, IDbConnection, etc. resolved in a DI scope per execution.

---

## Quick Start

```csharp
var builder = Host.CreateEmptyApplicationBuilder(args);

builder.Services.AddSaSchedule(b =>
{
    b.UseHostedService()
     .AddJob<CleanupJob>((sp, job) =>
     {
         job.EveryMinutes(5)
            .WithName("Database cleanup")
            .WithConcurrencyLimit(2)
            .ConfigureErrorHandling(err => err
                .IfErrorRetry(3)
                .ThenAbortJob());
     })
     .AddJob<ReportGenerationJob>(id: Guid.Parse("xxxx-xxxx"))
        .EveryHours(1)
        .StartImmediate();
});

var app = builder.Build();
await app.RunAsync();
```

---

## Defining Jobs

Jobs implement the `IJob` interface:

```csharp
public class CleanupJob : IJob
{
    private readonly ILogger<CleanupJob> _logger;
    private readonly IDbConnection _db;

    public CleanupJob(ILogger<CleanupJob> logger, IDbConnection db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running cleanup — iteration #{Num}", context.NumIterations);
        await _db.ExecuteAsync("DELETE FROM temp_table WHERE created_at < @now",
            new { now = DateTimeOffset.UtcNow }, cancellationToken);
    }
}
```

Scoped services (DbContext, IDbConnection, etc.) are resolved automatically within an DI scope per execution.

### Lambda Jobs

For quick one-off tasks without a dedicated class:

```csharp
b.AddJob((context, ct) =>
{
    Console.WriteLine($"Hello at {context.ExecuteAt}");
    return Task.CompletedTask;
}, jobId: Guid.NewGuid())
 .EverySeconds(10);
```

---

## Job Configuration (Builder API)

| Method | Description |
|---|---|
| `.WithName(string)` | Human-readable job name |
| `.StartImmediate()` | Execute on first start without waiting for the interval |
| `.RunOnce()` | Execute exactly once, then stop permanently |
| `.WithInitialDelay(TimeSpan)` | Delay before the first execution |
| `.EveryTime(TimeSpan, string?)` | Periodic interval with optional timing name |
| `.EverySeconds(int)` | Convenience alias for seconds |
| `.EveryMinutes(int)` | Convenience alias for minutes |
| `.EveryHours(int)` | Convenience alias for hours |
| `.EveryDays(int)` | Convenience alias for days |
| `.OnceIn(TimeSpan)` | Run once after a delay |
| `.WithCron(string, string?)` | Schedule using cron expression (minute hour dayOfMonth month dayOfWeek) |
| `.WithContextStackSize(int)` | Keep N previous contexts on a stack for debugging |
| `.WithTag(object)` | Attach arbitrary metadata |
| `.WithConcurrencyLimit(int)` | Number of concurrent executions |
| `.WithMaxConcurrency(int)` | Maximum slots allocated |
| `.WithShutdownTimeout(TimeSpan)` | How long `Stop`/shutdown waits for running iterations to finish (default 30s) |
| `.Disabled()` | Register but don't start |
| `.Merge(IJobProperties)` | Merge another configuration |
| `.ConfigureErrorHandling(Action<IJobErrorHandlingBuilder>)` | Error recovery policy |

---

## Cron Scheduling

5-field expression: `minute hour day-of-month month day-of-week`. Supports `*`, `,`, `-`, `/`.

```csharp
b.AddJob<DailyReport>().WithCron("0 9 * * *");       // Every day at 9:00 AM
b.AddJob<HealthCheck>().WithCron("*/15 * * * *");    // Every 15 minutes
b.AddJob<WeekdayCleanup>().WithCron("30 14 * * 1-5"); // Weekdays at 2:30 PM
```

---

## Concurrency Model

- **`ConcurrencyLimit`** — how many slots are actively running at any time (initially). Can be changed dynamically via `IJobScheduler.ConcurrencyLimit`.
- **`MaxConcurrency`** — total number of slot pre-allocated. `ConcurrencyLimit ≤ MaxConcurrency`.
- Dynamic adjustment pauses/resumes individual slots without recreating them.

---

## Error Handling

Each job defines its own error policy:

```csharp
.ConfigureErrorHandling(err => err
    .IfErrorRetry(count: 3)           // Retry up to 3 times
    .DoSuppressError(ex => ex is TimeoutException)  // Suppress timeouts silently
    .ThenAbortJob())                  // After retries exhausted, stop this job only
```

### Error Handling Actions

| Action | Behavior |
|---|---|
| `CloseApplication` | Stop the entire application via `IHostApplicationLifetime.StopApplication()` (**default**) |
| `AbortJob` | Stop only the current job; other jobs continue |
| `StopAllJobs` | Stop all registered jobs |

### Global Error Handler

Register a global handler that runs *before* per-job handling:

```csharp
b.AddErrorHandler((context, exception) =>
{
    // Return true to consume (suppress) the error
    // Return false to let per-job handling decide
    if (exception is InvalidOperationException)
    {
        context.Logger.LogWarning("Known issue: {Msg}", exception.Message);
        return true;
    }
    return false;
});
```

### JobException

When a job throws, it's wrapped in `JobException` containing:
- `JobContext` — full context at failure time
- `ContextSnapshot` — lightweight snapshot (scalar properties + stack depth), avoids expensive deep clone
- `InnerException` — the original exception

---

## Interceptors

Interceptors wrap every job execution, implementing chain-of-responsibility:

```csharp
public class LoggingInterceptor : IJobInterceptor
{
    private readonly ILogger<LoggingInterceptor> _logger;

    public LoggingInterceptor(ILogger<LoggingInterceptor> logger)
        => _logger = logger;

    public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken ct)
    {
        _logger.LogInformation("[{Job}] Starting", context.JobName);
        var sw = Stopwatch.StartNew();
        try
        {
            await next();
            sw.Stop();
            _logger.LogInformation("[{Job}] Completed in {Ms}ms", context.JobName, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[{Job}] Failed after {Ms}ms", context.JobName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}

// Register globally
b.AddInterceptor<LoggingInterceptor>();
```

Multiple interceptors can be registered — the first registered is the outermost wrapper, the last registered the innermost (closest to the job): `OnHandle` runs in registration order.

---

## Runtime Management

Access the scheduler via DI:

```csharp
public class Controller
{
    private readonly IScheduler _scheduler;

    public Controller(IScheduler scheduler)
        => _scheduler = scheduler;

    public async Task RestartAll()
    {
        var count = await _scheduler.Restart(TestContext.Current.CancellationToken);
        Console.WriteLine($"Restarted {count} jobs");
    }

    public async Task StopAll()
        => await _scheduler.Stop();

    public void ChangeConcurrency(Guid jobId, int newLimit)
    {
        var schedule = _scheduler.GetSchedule(jobId);
        schedule?.ConcurrencyLimit = newLimit;
    }
}
```

### IScheduler

| Member | Description |
|---|---|
| `Settings` | Schedule-wide settings |
| `Jobs` | Collection of `IJobScheduler` |
| `Start(ct)` | Start all non-disabled jobs |
| `Restart(ct)` | Stop + restart all started jobs |
| `Stop()` | Graceful stop; waits for running iterations up to each job's shutdown timeout (default 30s) |
| `GetSchedule(id)` | Find a specific job scheduler |

### IJobScheduler

| Member | Description |
|---|---|
| `JobId` | Unique identifier |
| `IsStarted` | Whether the job is currently running |
| `QueueTasks` | Tasks in the queue buffer (the pre-allocated slots while running, 0 when stopped) |
| `ConcurrencyLimit` | Get/set active concurrency |
| `StartChangeToken()` | Track start/stop state changes |
| `Start(ct)` | Start this job |
| `Stop()` | Stop with timeout |

---

## Best Practices

1. **Always use `UseHostedService()`** — integrates with Generic Host lifecycle
2. **Prefer typed jobs over lambdas** — better testability and DI resolution
3. **Set `ConcurrencyLimit` appropriately** — avoid overwhelming downstream systems
4. **Use `DoSuppressError` for transient failures** — don't crash on recoverable errors
5. **Add interceptors for cross-cutting concerns** — logging, metrics, distributed tracing
6. **Monitor via `IJobScheduler.IsStarted` and `QueueTasks`** — integrate with health checks
7. **Use `OnceIn(TimeSpan)` for migration jobs** — run once after deployment delay
8. **Disable jobs instead of removing** — useful for feature flags and gradual rollout

---

## License

MIT
