namespace Sa.Schedule.Engine;

internal interface IJobRunner
{
    /// <summary>
    /// Runs the job loop on the controller until it exits or is cancelled.
    /// The controller is always shut down on completion, even on failure.
    /// </summary>
    /// <param name="controller">The slot controller to run.</param>
    /// <param name="cancellationToken">
    /// The stop token for this job; cancellation ends the run and is not an error.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the loop ended because there is no more work:
    /// the run-once job completed, the schedule can never fire again (an
    /// unsatisfiable cron expression), or the error handling requested an abort.
    /// The scheduler turns this into its stop/teardown path, so it must not be
    /// reported while the loop is still supposed to run.
    /// <see langword="false"/> when the run was interrupted by a stop — the
    /// caller cancelled the token, so <see cref="IJobScheduler.Stop"/> already
    /// handled the shutdown and an abort path must not run again.
    /// </returns>
    Task<bool> Run(IJobController controller, CancellationToken cancellationToken);
}
