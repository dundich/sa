namespace Sa.Schedule.Settings;

internal class Job(Func<IJobContext, CancellationToken, Task> action) : IJob
{
    public Task Execute(IJobContext context, CancellationToken cancellationToken)
        => action(context, cancellationToken);
}
