namespace Sa.Schedule.Engine;


internal enum CanJobExecuteResult
{
    Ok,
    Abort
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

    /// <summary>
    /// True when the job was aborted because its error handling requested it
    /// (retries exhausted and the configured action was applied).
    /// </summary>
    bool AbortedByError { get; }

    void Pause();

    void Resume();


    ValueTask WaitToRun(CancellationToken cancellationToken);
    ValueTask WaitIfPaused(CancellationToken cancellationToken);
    ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken);

    Task Execute(CancellationToken cancellationToken);
    void ExecutionCompleted();
    void ExecutionFailed(Exception exception);
}
