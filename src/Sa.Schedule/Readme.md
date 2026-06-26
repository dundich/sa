# Sa.Schedule

The **Sa.Schedule** library provides a robust, production-ready framework for configuring and executing scheduled tasks in .NET applications. It supports periodic jobs, one-shot executions, dynamic concurrency control, error recovery strategies, interceptors, and graceful shutdown.

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
| `.Cron(string, string?)` | Schedule using cron expression (minute hour dayOfMonth month dayOfWeek) |
| `.WithContextStackSize(int)` | Keep N previous contexts on a stack for debugging |
| `.WithTag(object)` | Attach arbitrary metadata |
| `.WithConcurrencyLimit(int)` | Number of concurrent executions |
| `.WithMaxConcurrency(int)` | Maximum slots allocated |
| `.Disabled()` | Register but don't start |
| `.Merge(IJobProperties)` | Merge another configuration |
| `.ConfigureErrorHandling(Action<IJobErrorHandlingBuilder>)` | Error recovery policy |

### Cron Scheduling

Use cron expressions for precise scheduling control. The format follows standard 5-field cron:

```
minute hour day-of-month month day-of-week
```

**Supported features:**
- `*` — wildcard (any value)
- `,` — comma-separated list (e.g., `1,15,30`)
- `-` — range (e.g., `1-5`)
- `/` — step values (e.g., `*/5`, `1-20/3`)

**Examples:**

```csharp
// Every day at 9:00 AM
b.AddJob<DailyReport>()
 .Cron("0 9 * * *")
 .WithName("Daily report");

// Every 2 hours at minute 0
b.AddJob<HourlySync>()
 .Cron("0 */2 * * *")
 .WithName("Hourly sync");

// Weekdays (Mon-Fri) at 2:30 PM
b.AddJob<WeekdayCleanup>()
 .Cron("30 14 * * 1-5")
 .WithName("Weekday cleanup");

// First day of every month at midnight
b.AddJob[MonthlyBackup]()
 .Cron("0 0 1 * *")
 .WithName("Monthly backup");

// Every Monday, Wednesday, Friday at 6:00 AM
b.AddJob[TriWeeklyTask]()
 .Cron("0 6 * * 1,3,5")
 .WithName("Tri-weekly task");

// Every 15 minutes
b.AddJob[HealthCheck]()
 .Cron("*/15 * * * *")
 .WithName("Health check");

// Combined range and step: every 3rd hour from 9 AM to 5 PM
b.AddJob[BusinessMetrics]()
 .Cron("0 9-17/3 * * 1-5")
 .WithName("Business metrics");
```

**Advanced examples:**

```csharp
// Last day of month (approximate — use 28-31 and let cron filter)
b.AddJob[EndOfMonthReport]()
 .Cron("0 0 28-31 * *")
 .WithName("End of month report");

// Leap year only (Feb 29)
b.AddJob[LeapYearTask]()
 .Cron("0 0 29 2 *")
 .WithName("Leap year task");

// Multiple days of week (Mon, Wed, Fri at 9:00 and 17:00)
b.AddJob[PeakMonitor]()
 .Cron("0 9,17 * * 1,3,5")
 .WithName("Peak monitoring");
```

### Concurrency Model

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

Multiple interceptors can be registered — they apply in LIFO order (last added = outermost wrapper).

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
| `Schedules` | Collection of `IJobScheduler` |
| `Start(ct)` | Start all non-disabled jobs |
| `Restart(ct)` | Stop + restart all started jobs |
| `Stop()` | Graceful stop with 30s timeout |
| `GetSchedule(id)` | Find a specific job scheduler |

### IJobScheduler

| Member | Description |
|---|---|
| `JobId` | Unique identifier |
| `IsStarted` | Whether the job is currently running |
| `ActiveTasks` | Pending tasks in queue |
| `ConcurrencyLimit` | Get/set active concurrency |
| `StartChangeToken()` | Track start/stop state changes |
| `Start(ct)` | Start this job |
| `Stop()` | Stop with timeout |

---

## Architecture

```
DI Setup (Setup.cs + ScheduleBuilder.cs)
    ↓
Configuration (JobSettings, JobProperties, JobErrorHandling)
    ↓
Factory (JobFactory → creates IJobScheduler)
    ↓
Scheduler (IScheduler → manages IReadOnlyCollection<IJobScheduler>)
    ↓
JobScheduler (one per IJob, backed by SaWorkQueue)
    ↓
JobController (pre-allocated slots, pause/resume via SemaphoreSlim)
    ↓
JobExecutor (DI scope + interceptor chain)
    ↓
IJob.Execute(...)
```

---

## Best Practices

1. **Always use `UseHostedService()`** — integrates with Generic Host lifecycle
2. **Prefer typed jobs over lambdas** — better testability and DI resolution
3. **Set `ConcurrencyLimit` appropriately** — avoid overwhelming downstream systems
4. **Use `DoSuppressError` for transient failures** — don't crash on recoverable errors
5. **Add interceptors for cross-cutting concerns** — logging, metrics, distributed tracing
6. **Monitor via `IJobScheduler.IsStarted` and `ActiveTasks`** — integrate with health checks
7. **Use `OnceIn(TimeSpan)` for migration jobs** — run once after deployment delay
8. **Disable jobs instead of removing** — useful for feature flags and gradual rollout
