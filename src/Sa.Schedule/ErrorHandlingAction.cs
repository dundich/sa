namespace Sa.Schedule;

/// <summary>
/// Specifies the actions to be taken when an error occurs.
/// </summary>
public enum ErrorHandlingAction
{
    /// <summary>
    /// Closes the entire application.
    /// </summary>
    CloseApplication,
    /// <summary>
    /// Aborts the current job.
    /// </summary>
    AbortJob,
    /// <summary>
    /// Stops all running jobs.
    /// </summary>
    StopAllJobs
}