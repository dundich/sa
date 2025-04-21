namespace Sa.Schedule;

/// <summary>
/// Defines the contract for handling job errors.
/// </summary>
public interface IJobErrorHandler
{
    /// <summary>
    /// Handles an error that occurred during job execution.
    /// </summary>
    /// <param name="context">The job context.</param>
    /// <param name="exception">The exception that occurred.</param>
    void HandleError(IJobContext context, Exception exception);
}