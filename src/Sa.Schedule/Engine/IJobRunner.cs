namespace Sa.Schedule.Engine;

internal interface IJobRunner
{
    /// <summary>
    /// Runs the job loop on the controller until it exits.
    /// The controller is always shut down on completion, even on failure.
    /// </summary>
    /// <param name="controller">The slot controller to run.</param>
    /// <param name="cancellationToken">
    /// The stop token for this job; cancellation ends the run and is not an error.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the error handler requested the job to be aborted
    /// (retries exhausted and the configured action was applied);
    /// <see langword="false"/> for a normal exit (run-once completed, schedule exhausted,
    /// or a stop was requested).
    /// </returns>
    Task<bool> Run(IJobController controller, CancellationToken cancellationToken);
}
