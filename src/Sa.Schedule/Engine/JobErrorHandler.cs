using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sa.Schedule.Engine;

internal sealed partial class JobErrorHandler(
    IScheduleSettings settings, 
    IHostApplicationLifetime? lifetime, 
    ILogger<JobErrorHandler>? logger) : IJobErrorHandler
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
                LogAbortJob(context.JobName, exception.Message);
                break;

            case ErrorHandlingAction.CloseApplication:
                CloseApplication(context.JobName, context.JobServices.GetRequiredService<IScheduler>(), exception);
                break;

            case ErrorHandlingAction.StopAllJobs:
                StopAllJobs(context.JobName, context.JobServices.GetRequiredService<IScheduler>(), exception);
                break;

            default:
                LogUnknownJobError(exception, context.JobName);
                break;
        }

        throw context.LastError ?? exception;
    }

    private void StopAllJobs(string jobName, IScheduler scheduler, Exception exception)
    {
        LogStopAllJobs(jobName, exception.Message);

        if (scheduler == null) throw exception;
        scheduler.Stop();
    }

    private void CloseApplication(string jobName, IScheduler scheduler, Exception exception)
    {
        LogCloseApplication(jobName, exception.Message);

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

    [LoggerMessage(
        EventId = 501,
        Level = LogLevel.Error,
        Message = "[{JobName}] Unknown error")]
    partial void LogUnknownJobError(Exception exception, string jobName);


    [LoggerMessage(
        EventId = 502,
        Level = LogLevel.Error,
        Message = """
************
JOB: {JobName}
ERROR: The job will be aborted, the reason is an error:
{Error}
************
""")]
    partial void LogAbortJob(string jobName, string error);


    [LoggerMessage(
        EventId = 504,
        Level = LogLevel.Error,
        Message = """
************
JOB: {JobName}
ERROR: The application will be closed, the reason is an error:
{Error}
************
""")]
    partial void LogCloseApplication(string jobName, string error);


    [LoggerMessage(
        EventId = 503,
        Level = LogLevel.Error,
        Message = """
************
JOB: {JobName}
ERROR: The all jobs will be stopped, the reason is an error:
{Error}
************
""")]
    partial void LogStopAllJobs(string jobName, string error);
}
