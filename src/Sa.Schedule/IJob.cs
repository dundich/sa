namespace Sa.Schedule;

/// <summary>
/// Represents a job that can be executed.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the job.
    /// </summary>
    /// <param name="context">The job context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the execution of the job.</returns>
    Task Execute(IJobContext context, CancellationToken cancellationToken);
}