namespace Sa.Schedule.Engine;

/// <summary>
/// Why a job slot's loop ended.
/// </summary>
internal enum JobRunResult
{
    /// <summary>
    /// The loop ran out of work: the RunOnce budget was consumed, the schedule can
    /// never fire again, or the error handling requested an abort. The job is
    /// finished — a RunOnce job stays stopped permanently.
    /// </summary>
    Finished,

    /// <summary>
    /// The run was interrupted by the caller's stop token. <see cref="IJobScheduler.Stop"/>
    /// already moved the scheduler to the stopped state, so there is nothing to tear down.
    /// </summary>
    Stopped,

    /// <summary>
    /// The loop ended because something outside the iteration failed — a throwing
    /// custom <see cref="IJobTiming"/>, a disposal race, a paused-gate failure. This is
    /// <b>not</b> a natural completion: the RunOnce budget is not consumed, so the job
    /// stays restartable.
    /// </summary>
    Faulted
}

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
    /// Why the loop ended. The scheduler turns <see cref="JobRunResult.Finished"/> into
    /// the normal stop/teardown path (consuming the RunOnce budget) and
    /// <see cref="JobRunResult.Faulted"/> into the same teardown <i>without</i> consuming
    /// it, so a faulted RunOnce job can still be started again.
    /// </returns>
    Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken);
}
