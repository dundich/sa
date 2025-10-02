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
    // scope context
    public IJobContext Context { get; }

    // scope events
    ValueTask WaitToRun(CancellationToken cancellationToken);

    void Running();
    
    void Stopped(TaskStatus status);

    // iteration events
    ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken);
    
    Task Execute(CancellationToken cancellationToken);
    
    void ExecutionFailed(Exception exception);
    
    void ExecutionCompleted();
}
