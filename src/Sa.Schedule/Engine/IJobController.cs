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

    void Start();

    void Shutdown();

    bool IsPaused { get; }

    void Pause();

    void Resume();


    ValueTask WaitToRun(CancellationToken cancellationToken);
    ValueTask WaitIfPaused(CancellationToken cancellationToken);
    ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken);

    Task Execute(CancellationToken cancellationToken);
    void ExecutionCompleted();
    void ExecutionFailed(Exception exception);
}
