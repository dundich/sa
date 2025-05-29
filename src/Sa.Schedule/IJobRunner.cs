namespace Sa.Schedule;

public interface IJobRunner
{
    Task Run(IJobController controller, CancellationToken cancellationToken);
}
