namespace Sa.Schedule.Engine;

internal interface IJobRunner
{
    Task Run(IJobController controller, CancellationToken cancellationToken);
}
