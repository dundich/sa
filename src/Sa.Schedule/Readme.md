# Sa.Schedule

The Sa.Schedule library provides a way to configure and execute scheduled tasks. It allows you to manage a set of tasks that will be executed at a specific time or at a defined frequency.


## Example Usage

### Configuring Schedule DI

```csharp
Services.AddSaSchedule(b =>
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

### Job

A job implements the IJob interface and the Execute method, which contains the main logic.

```csharp
class SomeJob : IJob
{
    // IJobContext provides access to the execution context
    public async Task Execute(IJobContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
    }
}
```


## Managing Schedules

Management is done through the IScheduler and IJobScheduler interfaces.

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
