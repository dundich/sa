# Sa.Schedule

Планировщик задач для .NET — периодические и однократные задачи, динамический контроль параллелизма, стратегии восстановления, перехватчики, корректное завершение.

---

## Возможности

- **[Периодические и однократные задачи](#определение-задач-jobs)** — `EveryMinutes`, `EveryHours`, `RunOnce` и т.д.
- **[Cron-расписание](#cron-расписание)** — стандартные 5-полевые выражения.
- **[Динамический параллелизм](#модель-параллелизма)** — меняйте `ConcurrencyLimit` на лету.
- **[Стратегии восстановления](#обработка-ошибок)** — retry, подавление ошибок, остановка конкретной задачи или всего приложения.
- **[Перехватчики](#перехватчики-interceptors)** — chain-of-responsibility для логирования, метрик, трейсинга.
- **[Lambda-задачи](#lambda-задачи)** — анонимные делегаты без отдельных классов.
- **[Корректное завершение](#управление-во-время-выполнения)** — graceful shutdown с настраиваемым таймаутом.
- **[Scoped-сервисы](#определение-задач-jobs)** — DbContext, IDbConnection и прочее автоматически разрешаются в DI-scope при каждом выполнении.

---

## Быстрый старт

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

## Определение задач (Jobs)

Задачи реализуют интерфейс `IJob`:

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

Scoped-сервисы (DbContext, IDbConnection и т.д.) автоматически разрешаются в рамках DI-scope на каждое выполнение.

### Lambda-задачи

Для быстрых одноразовых задач без выделенного класса:

```csharp
b.AddJob((context, ct) =>
{
    Console.WriteLine($"Hello at {context.ExecuteAt}");
    return Task.CompletedTask;
}, jobId: Guid.NewGuid())
 .EverySeconds(10);
```

---

## Конфигурация задач (Builder API)

| Метод | Описание |
|-------|----------|
| `.WithName(string)` | Человекочитаемое имя задачи |
| `.StartImmediate()` | Выполнить при старте без ожидания интервала |
| `.RunOnce()` | Выполнить ровно один раз, затем навсегда остановиться |
| `.WithInitialDelay(TimeSpan)` | Задержка перед первым выполнением |
| `.EveryTime(TimeSpan, string?)` | Периодический интервал с опциональным именем тайминга |
| `.EverySeconds(int)` | Утилита для секунд |
| `.EveryMinutes(int)` | Утилита для минут |
| `.EveryHours(int)` | Утилита для часов |
| `.EveryDays(int)` | Утилита для дней |
| `.OnceIn(TimeSpan)` | Выполнить один раз после задержки |
| `.WithCron(string, string?)` | Расписание через cron-выражение (минута час деньМесяца месяц деньНедели) |
| `.WithContextStackSize(int)` | Хранить N предыдущих контекстов в стеке для отладки |
| `.WithTag(object)` | Прикрепить произвольные метаданные |
| `.WithConcurrencyLimit(int)` | Количество одновременных выполнений |
| `.WithMaxConcurrency(int)` | Максимальное количество зарезервированных слотов |
| `.WithShutdownTimeout(TimeSpan)` | Как долго `Stop`/shutdown ждёт завершения выполняющихся итераций (по умолчанию 30 сек) |
| `.Disabled()` | Зарегистрировать, но не запускать |
| `.Merge(IJobProperties)` | Объединить с другой конфигурацией |
| `.ConfigureErrorHandling(Action<IJobErrorHandlingBuilder>)` | Политика восстановления после ошибок |

---

## Cron-расписание

5-полевое выражение: `минута час деньМесяца месяц деньНедели`. Поддерживаются `*`, `,`, `-`, `/`.

```csharp
b.AddJob<DailyReport>().WithCron("0 9 * * *");       // Каждый день в 9:00
b.AddJob<HealthCheck>().WithCron("*/15 * * * *");    // Каждые 15 минут
b.AddJob<WeekdayCleanup>().WithCron("30 14 * * 1-5"); // Будни в 14:30
```

---

## Модель параллелизма

- **`ConcurrencyLimit`** — сколько слотов активно работают в любой момент (изначально). Можно менять динамически через `IJobScheduler.ConcurrencyLimit`.
- **`MaxConcurrency`** — общее количество предзарезервированных слотов. `ConcurrencyLimit ≤ MaxConcurrency`.
- Динамическая корректировка приостанавливает/возобновляет отдельные слоты без их пересоздания.

---

## Обработка ошибок

Каждая задача определяет свою собственную политику ошибок:

```csharp
.ConfigureErrorHandling(err => err
    .IfErrorRetry(count: 3)           // Повторить до 3 раз
    .DoSuppressError(ex => ex is TimeoutException)  // Подавить таймауты молча
    .ThenAbortJob())                  // После исчерпания повторов остановить только эту задачу
```

### Действия обработки ошибок

| Действие | Поведение |
|----------|----------|
| `CloseApplication` | Остановить всё приложение через `IHostApplicationLifetime.StopApplication()` (**по умолчанию**) |
| `AbortJob` | Остановить только текущую задачу; другие продолжат работу |
| `StopAllJobs` | Остановить все зарегистрированные задачи |

### Глобальный обработчик ошибок

Зарегистрируйте глобальный обработчик, который выполняется *перед* обработкой на уровне задачи:

```csharp
b.AddErrorHandler((context, exception) =>
{
    // Верните true для поглощения (подавления) ошибки
    // Верните false, чтобы передать обработку на уровень задачи
    if (exception is InvalidOperationException)
    {
        context.Logger.LogWarning("Known issue: {Msg}", exception.Message);
        return true;
    }
    return false;
});
```

### JobException

Когда задача выбрасывает исключение, оно оборачивается в `JobException`, содержащий:
- `JobContext` — полный контекст в момент сбоя
- `ContextSnapshot` — лёгкий снимок (скалярные свойства + глубина стека), избегает дорогого глубокого клонирования
- `InnerException` — оригинальное исключение

---

## Перехватчики (Interceptors)

Перехватчики оборачивают каждое выполнение задачи, реализуя chain-of-responsibility:

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

// Регистрация глобально
b.AddInterceptor<LoggingInterceptor>();
```

Можно зарегистрировать несколько перехватчиков — первый зарегистрированный становится внешней обёрткой, последний добавленный — внутренней (ближайшей к задаче); `OnHandle` вызывается в порядке регистрации.

---

## Управление во время выполнения

Получите доступ к планировщику через DI:

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

| Член | Описание |
|------|----------|
| `Settings` | Настройки на весь план |
| `Jobs` | Коллекция `IJobScheduler` |
| `Start(ct)` | Запустить все незаблокированные задачи |
| `Restart(ct)` | Остановить + перезапустить все запущенные задачи |
| `Stop()` | Корректная остановка; ждёт завершения выполняющихся итераций до таймаута shutdown каждой задачи (по умолчанию 30 сек) |
| `GetSchedule(id)` | Найти конкретный планировщик задач |

### IJobScheduler

| Член | Описание |
|------|----------|
| `JobId` | Уникальный идентификатор |
| `IsStarted` | Запущена ли задача в данный момент |
| `QueueTasks` | Задачи в буфере очереди (зарезервированные слоты во время работы, 0 когда остановлена) |
| `ConcurrencyLimit` | Получить/установить активный параллелизм |
| `StartChangeToken()` | Отслеживать изменения состояния start/stop |
| `Start(ct)` | Запустить эту задачу |
| `Stop()` | Остановить с таймаутом |

---

## Лучшие практики

1. **Всегда используйте `UseHostedService()`** — интеграция с жизненным циклом Generic Host
2. **Предпочитайте типизированные задачи lambda-задачам** — лучшая тестируемость и разрешение DI
3. **Устанавливайте `ConcurrencyLimit` appropriately** — не перегружайте downstream-системы
4. **Используйте `DoSuppressError` для транзитных сбоев** — не падайте на восстанавливаемых ошибках
5. **Добавляйте перехватчики для сквозных задач** — логирование, метрики, распределённый трейсинг
6. **Мониторьте через `IJobScheduler.IsStarted` и `QueueTasks`** — интегрируйте с health checks
7. **Используйте `OnceIn(TimeSpan)` для миграционных задач** — выполнить один раз после задержки деплоя
8. **Отключайте задачи вместо удаления** — полезно для feature flags и постепенного rollout

---

## Лицензия

MIT
