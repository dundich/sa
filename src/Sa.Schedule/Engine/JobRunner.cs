using System.Diagnostics;

namespace Sa.Schedule.Engine;


internal sealed class JobRunner() : IJobRunner
{
    public async Task Run(IJobController controller, CancellationToken cancellationToken)
    {
        await controller.WaitToRun(cancellationToken);

        controller.Init();

        try
        {
            await RunLoop(controller, cancellationToken);
        }
        finally
        {
            controller.Finish();
        }
    }

    [StackTraceHidden]
    private static async Task RunLoop(IJobController controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await controller.WaitIfPaused(cancellationToken);

            CanJobExecuteResult next = await controller.CanExecute(cancellationToken);

            switch (next)
            {
                case CanJobExecuteResult.Abort:
                    return;

                case CanJobExecuteResult.Skip:
                    continue;

                case CanJobExecuteResult.Ok:
                    await ExecuteIteration(controller, cancellationToken);
                    break;
            }
        }
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
