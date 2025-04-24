using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sa.Schedule.Settings;
using Sa.Timing.Providers;

namespace Sa.Schedule.Engine;


/// <summary>
/// job lifecycly controller with context
/// </summary>
internal partial class JobController(IJobSettings settings, IInterceptorSettings interceptorSettings, IServiceScopeFactory scopeFactory) : IJobController
{
    private readonly JobContext context = new(settings);

    private JobPipeline? _job;

    public IJobContext Context => context;

    public DateTimeOffset UtcNow => context.JobServices.GetService<ICurrentTimeProvider>()?.GetUtcNow()
        ?? DateTimeOffset.UtcNow;

    public async ValueTask WaitToRun(CancellationToken cancellationToken)
    {
        if (context.NumRuns == 0 && settings.Properties.InitialDelay.HasValue && settings.Properties.InitialDelay.Value != TimeSpan.Zero)
        {
            context.Status = JobStatus.WaitingToRun;
            await Task.Delay(settings.Properties.InitialDelay.Value, cancellationToken);
        }
    }

    public void Running()
    {
        _job = new JobPipeline(settings, interceptorSettings, scopeFactory);

        context.JobServices = _job.JobServices;
        context.Status = JobStatus.Running;

        if (context.NumRuns == 0) context.CreatedAt = UtcNow;
        context.NumRuns++;
    }

    public void Stopped(TaskStatus status)
    {
        switch (status)
        {
            case TaskStatus.Faulted: context.Status = JobStatus.Failed; break;
            case TaskStatus.Canceled: context.Status = JobStatus.Cancelled; break;
            case TaskStatus.RanToCompletion: context.Status = JobStatus.Completed; break;
        }

        context.JobServices = NullJobServices.Instance;
        _job?.Dispose();
    }

    public async ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken)
    {
        if (settings.Properties.IsRunOnce == true && context.NumIterations > 0)
            return CanJobExecuteResult.Abort;

        if (context.NumIterations == 0 && settings.Properties.Immediate == true)
            return CanJobExecuteResult.Ok;

        IJobTiming? timing = settings.Properties.Timing;

        if (timing != null)
        {
            DateTimeOffset now = UtcNow;

            DateTimeOffset? next = timing.GetNextOccurrence(now, context);

            if (!next.HasValue)
                return CanJobExecuteResult.Abort;

            TimeSpan delay = next.Value - now;

            if (delay.TotalMilliseconds > 0)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        int stackSize = settings.Properties.ContextStackSize.GetValueOrDefault();

        if (stackSize > 0)
        {
            if (context.Stack.Count == stackSize) context.Stack.Dequeue();
            context.Stack.Enqueue(context.Clone());
        }

        return !cancellationToken.IsCancellationRequested
            ? CanJobExecuteResult.Ok
            : CanJobExecuteResult.Abort;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        context.NumIterations++;
        context.ExecuteAt = UtcNow;
        return _job!.Execute(Context, cancellationToken);
    }

    public void ExecutionCompleted()
    {
        context.CompetedIterations++;
        context.FailedRetries = 0;
    }

    public void ExecutionFailed(Exception exception)
    {
        JobException error = new(context, exception);
        context.FailedIterations++;
        context.LastError = error;

        IJobErrorHandling errorHandling = settings.ErrorHandling;

        if (errorHandling.HasSuppressError && errorHandling.SuppressError?.Invoke(exception) == true)
        {
            LogJobWasSuppressed(context.Logger, context.JobName, exception.Message);
            return;
        }

        if (context.FailedRetries < settings.ErrorHandling.RetryCount)
        {
            context.FailedRetries++;
            LogFailedRetryAttempts(context.Logger, context.JobName, context.FailedRetries, errorHandling.RetryCount, exception.Message);
            return;
        }

        context.JobServices.GetService<IJobErrorHandler>()?.HandleError(Context, error);
    }


    [LoggerMessage(
        EventId = 401,
        Level = LogLevel.Warning,
        Message = "[{JobName}] the error: “{Error}” on job was suppressed to continue.")]
    static partial void LogJobWasSuppressed(ILogger logger, string jobName, string error);

    [LoggerMessage(
        EventId = 402,
        Level = LogLevel.Warning,
        Message = "[{JobName}] {FailedRetryAttempts} out of {RetryCount} reps when the job failed due to an error: “{Error}”")]
    static partial void LogFailedRetryAttempts(ILogger logger, string jobName, int failedRetryAttempts, int retryCount, string error);
}
