using System.Diagnostics;

namespace Sa.Schedule.Engine;

internal sealed class JobRunner() : IJobRunner
{
    public async Task<bool> Run(IJobController controller, CancellationToken cancellationToken)
    {
        bool aborted = false;

        try
        {
            await controller.WaitToRun(cancellationToken);

            controller.Start();

            aborted = await RunLoop(controller, cancellationToken);
        }
        finally
        {
            // Always shut down the controller (and its DI scope) — also on
            // wait/start failure, otherwise the scope would leak.
            controller.Shutdown();
        }

        return aborted;
    }

    [StackTraceHidden]
    private static async Task<bool> RunLoop(IJobController controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await controller.WaitIfPaused(cancellationToken);

            if (await controller.CanExecute(cancellationToken) == CanJobExecuteResult.Abort)
            {
                // Any abort exit is "no more work" — the job completed its
                // run-once, its schedule can never fire, or the error handling
                // aborted it. The caller (the scheduler) treats every one of
                // these as a job that has finished, in contrast to a cancelled
                // run below which is a stop the caller already knows about.
                return true;
            }

            await ExecuteIteration(controller, cancellationToken);
        }

        return false;
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
