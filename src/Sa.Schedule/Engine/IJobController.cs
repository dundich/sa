namespace Sa.Schedule.Engine;


internal enum CanJobExecuteResult
{
    Ok,
    Abort,
    Skip
}

/// <summary>
/// Defines the contract for a job controller, responsible for managing the lifecycle of a job.
/// </summary>
internal interface IJobController
{
    int Index { get; }

    ValueTask WaitToRun(CancellationToken cancellationToken);

    void Init();

    void Finish();



    // Методы управления паузой
    bool IsPaused { get; }

    void Pause();

    void Resume();

    ValueTask WaitIfPaused(CancellationToken cancellationToken);



    // iteration events

    ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken);

    Task Execute(CancellationToken cancellationToken);
    void ExecutionCompleted();
    void ExecutionFailed(Exception exception);
}
