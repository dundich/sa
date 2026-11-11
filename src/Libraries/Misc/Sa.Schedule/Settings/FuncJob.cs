namespace Sa.Schedule.Settings;

internal sealed class FuncJob(Func<IJobContext, CancellationToken, Task> action) : IJob
{
    public Task ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
        => action(context, cancellationToken);
}
