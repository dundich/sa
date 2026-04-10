using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;


/// <summary>
/// job lifecycly controller with context
/// </summary>
internal sealed partial class JobController(
    IJobSettings settings,
    IInterceptorSettings interceptorSettings,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IJobController
{

    private readonly JobContext _context = new(settings);

    private volatile JobPipeline? _job;

    public async ValueTask WaitToRun(CancellationToken cancellationToken)
    {
        if (_context.NumRuns == 0
            && settings.Properties.InitialDelay.HasValue
            && settings.Properties.InitialDelay.Value != TimeSpan.Zero)
        {
            await Task.Delay(settings.Properties.InitialDelay.Value, cancellationToken);
        }
    }

    public void Running()
    {
        _job = new JobPipeline(settings, interceptorSettings, scopeFactory);

        _context.ServiceProvider = _job.ServiceProvider;

        if (_context.NumRuns == 0) _context.CreatedAt = timeProvider.GetUtcNow();
        _context.NumRuns++;
    }

    public void Stopped(TaskStatus status)
    {
        _context.ServiceProvider = NullJobServices.Instance;
        _job?.Dispose();
    }

    public async ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken)
    {
        if (settings.Properties.IsRunOnce == true && _context.NumIterations > 0)
            return CanJobExecuteResult.Abort;

        if (_context.NumIterations == 0 && settings.Properties.Immediate == true)
            return CanJobExecuteResult.Ok;

        IJobTiming? timing = settings.Properties.Timing;

        if (timing != null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();

            DateTimeOffset? next = timing.GetNextOccurrence(now, _context);

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
            if (_context.Stack.Count == stackSize) _context.Stack.Dequeue();
            _context.Stack.Enqueue(_context.Clone());
        }

        return !cancellationToken.IsCancellationRequested
            ? CanJobExecuteResult.Ok
            : CanJobExecuteResult.Abort;
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        _context.NumIterations++;
        _context.ExecuteAt = timeProvider.GetUtcNow();
        return _job!.Execute(_context, cancellationToken);
    }

    public void ExecutionCompleted()
    {
        _context.CompetedIterations++;
        _context.FailedRetries = 0;
    }

    public void ExecutionFailed(Exception exception)
    {
        JobException error = new(_context, exception);
        _context.FailedIterations++;
        _context.LastError = error;

        IJobErrorHandling errorHandling = settings.ErrorHandling;

        if (errorHandling.HasSuppressError
            && errorHandling.SuppressError?.Invoke(exception) == true)
        {
            LogJobWasSuppressed(
                _context.Logger,
                _context.JobName,
                exception.GetType().Name,
                exception.Message);
            return;
        }

        if (_context.FailedRetries < settings.ErrorHandling.RetryCount)
        {
            _context.FailedRetries++;
            LogFailedRetryAttempts(
                _context.Logger,
                _context.JobName,
                _context.FailedRetries,
                errorHandling.RetryCount,
                exception.GetType().Name,
                exception.Message);
            return;
        }

        _context.ServiceProvider.GetService<IJobErrorHandler>()?.HandleError(_context, error);
    }


    [LoggerMessage(
        EventId = 401,
        Level = LogLevel.Warning,
        Message = "[{JobName}] the error: {Type} “{Error}” on job was suppressed to continue.")]
    static partial void LogJobWasSuppressed(ILogger logger, string jobName, string type, string error);

    [LoggerMessage(
        EventId = 402,
        Level = LogLevel.Warning,
        Message = "[{JobName}] {FailedRetryAttempts} out of {RetryCount} reps when the job failed due to an error: {Type} “{Error}”")]
    static partial void LogFailedRetryAttempts(
        ILogger logger, string jobName, int failedRetryAttempts, int retryCount, string type, string error);
}
