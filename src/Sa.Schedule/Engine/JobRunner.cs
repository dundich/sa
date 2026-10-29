using System.Diagnostics;

namespace Sa.Schedule.Engine;

internal sealed class JobRunner() : IJobRunner
{
    public async Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
    {
        JobRunResult result;

        try
        {
            await controller.WaitToRun(cancellationToken);

            // Do not open the iteration scope while the slot is paused: with the
            // reader pool held at MaxConcurrency every pre-allocated slot runs this
            // loop, and an inactive one must not pin a DI scope (DbContext,
            // connection, ...) for as long as it stays paused. RunLoop re-checks the
            // gate on every iteration, so this is only the initial barrier.
            await controller.WaitIfPaused(cancellationToken);

            controller.Start();

            result = await RunLoop(controller, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A stop that landed during the initial delay or the first pause check.
            result = JobRunResult.Stopped;
        }
        finally
        {
            // Always shut down the controller (and its DI scope) — also on
            // wait/start failure, otherwise the scope would leak.
            controller.Shutdown();
        }

        return result;
    }

    [StackTraceHidden]
    private static async Task<JobRunResult> RunLoop(IJobController controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await controller.WaitIfPaused(cancellationToken);

                if (await controller.CanExecute(cancellationToken) == CanJobExecuteResult.Abort)
                {
                    // Any abort exit is "no more work" — the job completed its
                    // run-once, its schedule can never fire, or the error handling
                    // aborted it. The caller (the scheduler) treats every one of
                    // these as a job that has finished, in contrast to a cancelled
                    // run below which is a stop the caller already knows about.
                    return JobRunResult.Finished;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled the stop token (Stop/Dispose), so IsStarted is
                // already false — this is not an error, the run just ends.
                return JobRunResult.Stopped;
            }
            catch (Exception ex)
            {
                // A failure outside the iteration itself: a throwing custom
                // IJobTiming, a disposal race, a paused-gate failure. Report it to the
                // job's error policy and end the run — the loop state is no longer
                // known, and continuing would spin on the same failure at full speed.
                // "Faulted" (not "Finished") keeps a RunOnce budget intact, so the job
                // can be started again after the fault is fixed.
                controller.ExecutionFailed(ex);
                return JobRunResult.Faulted;
            }

            await ExecuteIteration(controller, cancellationToken);
        }

        return JobRunResult.Stopped;
    }

    private static async Task ExecuteIteration(
        IJobController controller,
        CancellationToken cancellationToken)
    {
        try
        {
            await controller.Execute(cancellationToken);
            controller.ExecutionCompleted();
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
        {
            // Expected cancellation - silently exit
        }
        catch (Exception ex)
        {
            controller.ExecutionFailed(ex);
        }
    }
}
