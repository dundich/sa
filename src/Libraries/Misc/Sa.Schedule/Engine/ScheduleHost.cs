using Microsoft.Extensions.Hosting;

namespace Sa.Schedule.Engine;

internal sealed class ScheduleHost(IScheduler scheduler) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await scheduler.Start(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop = scheduler.Stop();

        try
        {
            // Honour the host's shutdown deadline: a job that ignores cancellation is
            // already bounded by its own shutdown timeout, but the host's token may be
            // shorter and must not be exceeded silently.
            await stop.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host deadline won. The bounded per-job teardown keeps running on its
            // own; observe its eventual exception so it is not reported as unobserved.
            _ = stop.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
