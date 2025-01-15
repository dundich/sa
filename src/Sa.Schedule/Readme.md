# Sa.Schedule

Библиотека Sa.Schedule предоставляет способ настройки и выполнения задач по расписанию. Позволяет управлять набором задач, которые будут выполняться в определенное время или по определенной периодичности.


## Пример использования

### Конфигурирование расписания

```csharp
Services.AddSchedule(b =>
{
    b.AddJob<SomeJob>((sp, builder) =>
    {
        builder
            .EveryTime(TimeSpan.FromMilliseconds(100))
            .RunOnce()
            .StartImmediate();
    });
});
```

### Задание (Job)

Задание реализует интерфейс IJob и метод Execute, который выполняет основную логику

```csharp
class SomeJob : IJob
{
    // IJobContext предоставляет доступ к контексту выполнения
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken); // Имитация работы
    }
}
```


## Управление расписаниями

Осуществлеятся посредством интерфейсов IScheduler и IJobScheduler.

```csharp
/// <summary>
/// This scheduler that manages multiple job schedulers.
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Gets the schedule settings.
    /// </summary>
    IScheduleSettings Settings { get; }

    /// <summary>
    /// Gets the collection of job schedulers.
    /// </summary>
    IReadOnlyCollection<IJobScheduler> Schedules { get; }

    /// <summary>
    /// Starts the scheduler.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of jobs started.</returns>
    int Start(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the scheduler.
    /// </summary>
    int Restart();

    /// <summary>
    /// Stops the scheduler.
    /// </summary>
    Task Stop();
}

/// <summary>
/// This individual task scheduler is responsible for managing specific tasks.
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Gets a value indicating whether the job scheduler is currently active.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Gets the context associated with the job scheduler.
    /// </summary>
    IJobContext Context { get; }

    /// <summary>
    /// Gets a change token that can be used to track changes to the active state of the scheduler.
    /// </summary>
    IChangeToken GetActiveChangeToken();

    /// <summary>
    /// Starts the job scheduler asynchronously.
    /// </summary>
    bool Start(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts the job scheduler.
    /// </summary>
    bool Restart();

    /// <summary>
    /// Stops the job scheduler.
    /// </summary>
    Task Stop();
}

```