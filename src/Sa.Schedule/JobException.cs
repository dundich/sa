using Sa.Schedule.Engine;

namespace Sa.Schedule;

/// <summary>
/// Represents an error that occurred during job execution.
/// </summary>
/// <param name="context">The job context at the time of the error.</param>
/// <param name="innerException">The underlying exception that caused this error.</param>
public class JobException(IJobContext context, Exception? innerException)
    : Exception($"[{context.JobName}] job error", innerException)
{
    /// <summary>
    /// Gets a lightweight snapshot of the job context at the time of the error.
    /// Contains only scalar properties and stack depth (capped at 10).
    /// Avoids cloning the full context stack.
    /// </summary>
    public IJobSnapshot ContextSnapshot { get; } = new JobSnapshot(context);
}
