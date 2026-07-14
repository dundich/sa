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
        scheduler?.Stop();
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
