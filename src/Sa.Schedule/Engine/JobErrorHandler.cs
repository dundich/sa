using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal sealed class JobErrorHandler(
    IScheduleSettings settings,
    IHostApplicationLifetime? lifetime,
    ILogger<JobErrorHandler>? logger) : IJobErrorHandler
{
    public void HandleError(IJobContext context, Exception exception)
    {
        if (settings.HandleError?.Invoke(context, exception) == true)
        {
            // Global handler consumed the error — do not rethrow
            return;
        }

        DoHandleError(context, exception);
    }

    private void DoHandleError(IJobContext context, Exception exception)
    {
        switch (context.Settings.ErrorHandling.ThenAction)
        {
            case ErrorHandlingAction.AbortJob:
                logger?.LogAbortJob(context.JobName, exception.ToString());
                break;

            case ErrorHandlingAction.CloseApplication:
                CloseApplication(context.JobName, exception);
                break;

            case ErrorHandlingAction.StopAllJobs:
                StopAllJobs(context.JobName, exception, context);
                break;

            default:
                logger?.LogUnknownJobError(context.JobName, exception.ToString());
                break;
        }

        throw context.LastError ?? exception;
    }

    private void StopAllJobs(string jobName, Exception exception, IJobContext context)
    {
        logger?.LogStopAllJobs(jobName, exception.ToString());

        var scheduler = context.ServiceProvider.GetService<IScheduler>();

        if (scheduler is null) return;

        // This runs on the failing job's own queue reader, and that reader is still
        // working on the very item that got us here: stopping *this* job would park
        // in WaitForIdleAsync waiting for its own item — a guaranteed stall for the
        // whole shutdown timeout before it gives up. The other jobs can go idle, so
        // they are stopped here; this one stops itself right after, because
        // DoHandleError always throws below — the controller catches that as
        // "action applied", sets AbortedByError and the abort path tears its
        // readers down.
        Guid self = context.Settings.JobId;

        List<Task> stops = [];

        foreach (IJobScheduler job in scheduler.Jobs)
        {
            if (job.JobId == self) continue;

            stops.Add(job.Stop());
        }

        try
        {
            Task.WhenAll(stops).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // A Stop() that failed must not hide the original error: the configured
            // action is applied below regardless.
            logger?.LogStopAllJobsIncomplete(jobName, ex.ToString());
        }
    }

    private void CloseApplication(string jobName, Exception exception)
    {
        logger?.LogCloseApplication(jobName, error: exception.ToString());

        if (lifetime == null) return;

        if (lifetime is IHostApplicationLifetime hostAppLifetime)
        {
            // Safe fire-and-forget — StopApplication is designed for this
            hostAppLifetime.StopApplication();
        }
    }
}
