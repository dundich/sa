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
                return controller.AbortedByError;
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
