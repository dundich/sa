using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal class JobErrorHandler(IScheduleSettings settings, IHostApplicationLifetime? lifetime, ILogger<JobErrorHandler>? logger) : IJobErrorHandler
{
    public void HandleError(IJobContext context, Exception exception)
    {
        if (settings.HandleError?.Invoke(context, exception) != true)
        {
            // default handle
            DoHandleError(context, exception);
        }

        throw exception;
    }

    private void DoHandleError(IJobContext context, Exception exception)
    {
        switch (context.Settings.ErrorHandling.ThenAction)
        {
            case ErrorHandlingAction.AbortJob:
                AbortJob(context.JobName, exception);
                break;

            case ErrorHandlingAction.CloseApplication:
                CloseApplication(context.JobName, context.JobServices.GetRequiredService<IScheduler>(), exception);
                break;

            case ErrorHandlingAction.StopAllJobs:
                StopAllJobs(context.JobName, context.JobServices.GetRequiredService<IScheduler>(), exception);
                break;

            default:
                logger?.LogError(exception, "[{JobName}] Unknown error", context.JobName);
                break;
        }

        throw context.LastError ?? exception;
    }

    private void AbortJob(string jobName, Exception exception)
    {
        logger?.LogError(exception, @"
************
JOB: {JobName}
ERROR: The job will be aborted, the reason is an error:
{Error}
************
        ", jobName, exception.Message);
    }

    private void StopAllJobs(string jobName, IScheduler scheduler, Exception exception)
    {
        logger?.LogError(exception, @"
************
JOB: {JobName}
ERROR: The all jobs will be stoped, the reason is an error:
{Error}
************
        ", jobName, exception.Message);


        if (scheduler == null) throw exception;
        scheduler.Stop();
    }

    private void CloseApplication(string jobName, IScheduler scheduler, Exception exception)
    {
        logger?.LogError(exception, @"
************
JOB: {JobName}
ERROR: The application will be closed, the reason is an error:
{Error}
************
    ", jobName, exception.Message);

        if (lifetime == null) throw exception;

        if (scheduler.Settings.IsHostedService)
        {
            lifetime.StopApplication();
        }
        else
        {
            scheduler.Stop().ContinueWith(_ => lifetime.StopApplication());
        }
    }
}
