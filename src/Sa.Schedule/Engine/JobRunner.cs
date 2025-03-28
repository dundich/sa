using System.Diagnostics;

namespace Sa.Schedule.Engine;


internal class JobRunner() : IJobRunner
{
    public async Task Run(IJobController controller, CancellationToken cancellationToken)
    {
        await controller.WaitToRun(cancellationToken);

        controller.Running();

        await RunLoop(controller, cancellationToken)
            .ContinueWith(t => controller.Stopped(t.Status), CancellationToken.None);
    }

    [StackTraceHidden]
    private static async Task RunLoop(IJobController controller, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            CanJobExecuteResult next = await controller.CanExecute(cancellationToken);

            if (next == CanJobExecuteResult.Abort) break;
            if (next == CanJobExecuteResult.Skip) continue;

            try
            {
                await controller.Execute(cancellationToken);
                controller.ExecutionCompleted();
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                // skip
            }
            catch (Exception ex)
            {
                controller.ExecutionFailed(ex);
            }
        }
    }
}

