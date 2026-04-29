namespace Sa.Utils.WorkQueue;

public enum SaWorkStatus
{
    /// <summary>
    /// in processing
    /// </summary>
    Running,
    /// <summary>
    /// it`s ok
    /// </summary>
    Completed,
    /// <summary>
    /// Unhandled technical error, may or may not be retried depending on policy.
    /// </summary>
    Faulted,
    /// <summary>
    /// Operation was cancelled by the system (timeout, shutdown, external trigger).
    /// Automatic retry is permissible.
    /// </summary>
    Cancelled,
    /// <summary>
    /// Operation was intentionally aborted by user or orchestrator.
    /// Automatic retry MUST NOT be performed.
    /// </summary>
    Aborted
}
