using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sa.Schedule.Settings;

namespace Sa.Schedule.Engine;


/// <summary>
/// job lifecycly controller with context
/// </summary>
internal sealed partial class JobController(
    int index,
    IJobSettings settings,
    IInterceptorSettings interceptorSettings,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IJobController, IDisposable
{

    private readonly JobContext _context = new(settings);

    // Pause gate: _isPaused is the single source of truth and is only read
    // and written under _pauseSync, so the "am I paused" decision is atomic
    // with respect to Pause/Resume. While paused, _pauseWaiters holds the
    // TaskCompletionSource that WaitIfPaused awaits — the await YIELDS
    // instead of blocking the calling thread: a reader is spawned
    // synchronously by the queue (StartReaderUnderLock, under the queue's
    // reader lock) and runs the loop on the caller's thread until the first
    // real yield, so a thread-blocking gate would deadlock the very call
    // that Resumes the controller next.
    private readonly Lock _pauseSync = new();
    private volatile bool _isPaused;
    private volatile TaskCompletionSource<bool>? _pauseWaiters;

    private volatile bool _disposed;
    private volatile bool _abortedByError;
    private volatile JobExecutor? _executor;
    private readonly CancellationTokenSource _shutdownCts = new();


    public int Index => index;
    public bool IsPaused => _isPaused;
    public bool AbortedByError => _abortedByError;


    public async ValueTask WaitToRun(CancellationToken cancellationToken)
    {
        if (_disposed) return;

        if (_context.NumRuns == 0
            && settings.Properties.InitialDelay is { } delay
            && delay != TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }


    public void Pause()
    {
        if (_disposed) return;

        lock (_pauseSync)
        {
            if (_isPaused) return;

            _isPaused = true;
            _pauseWaiters = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        if (_disposed) return;

        lock (_pauseSync)
        {
            if (!_isPaused) return;

            _isPaused = false;
            var waiters = _pauseWaiters;
            _pauseWaiters = null;
            waiters?.TrySetResult(true);
        }
    }

    public async ValueTask WaitIfPaused(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_disposed) throw new OperationCanceledException(cancellationToken);

            bool paused;
            TaskCompletionSource<bool>? waiters;

            lock (_pauseSync)
            {
                paused = _isPaused;
                waiters = _pauseWaiters;
            }

            if (!paused) return;

            // (paused, no waiters) is only reachable after Shutdown tore the
            // gate down — same exit as a cancelled wait.
            if (waiters is null) throw new OperationCanceledException(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdownCts.Token);

            try
            {
                await waiters.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // WaitAsync throws TaskCanceledException (a subclass) —
                // normalize to the plain OCE the caller's loop expects.
                throw new OperationCanceledException(cancellationToken);
            }

            // Woken by a resume — re-check the state: the job may have been
            // re-paused (or shut down) while we were waiting.
        }
    }

    public void Start()
    {
        if (_disposed) return;

        _executor = new JobExecutor(settings, interceptorSettings, scopeFactory);

        _context.ServiceProvider = _executor.ServiceProvider;

        if (_context.NumRuns == 0) _context.CreatedAt = timeProvider.GetUtcNow();
        _context.NumRuns++;
    }

    public void Shutdown()
    {
        if (_disposed) return;

        _disposed = true;
        _shutdownCts.Cancel();

        // Complete the pause gate so a waiter parked on it can exit;
        // waiters that observe the _shutdownCts cancellation first
        // leave via the linked-token OCE instead.
        TaskCompletionSource<bool>? waiters;
        lock (_pauseSync)
        {
            waiters = _pauseWaiters;
            _pauseWaiters = null;
        }
        waiters?.TrySetResult(true);

        _context.ServiceProvider = NullJobServices.Instance;
        _executor?.Dispose();
        _shutdownCts.Dispose();
    }

    public async ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken)
    {
        if (_abortedByError)
            return CanJobExecuteResult.Abort;

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

        PushStackSnapshot();

        return !cancellationToken.IsCancellationRequested
            ? CanJobExecuteResult.Ok
            : CanJobExecuteResult.Abort;
    }

    private void PushStackSnapshot()
    {
        int stackSize = settings.Properties.ContextStackSize.GetValueOrDefault();

        if (stackSize > 0)
        {
            if (_context.Stack.Count == stackSize) _context.Stack.Dequeue();
            _context.Stack.Enqueue(_context.ToSnapshot());
        }
    }

    public Task Execute(CancellationToken cancellationToken)
    {
        _context.NumIterations++;
        _context.ExecuteAt = timeProvider.GetUtcNow();
        return _executor!.Execute(_context, cancellationToken);
    }

    public void ExecutionCompleted()
    {
        _context.CompletedIterations++;
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

        int retryCount = errorHandling.RetryCount ?? JobErrorHandling.Default.RetryCount;

        // Track retry count. The scheduler decides whether to re-enqueue the job
        // based on FailedRetries < RetryCount.
        if (_context.FailedRetries < retryCount)
        {
            _context.FailedRetries++;
            LogFailedRetryAttempts(
                _context.Logger,
                _context.JobName,
                _context.FailedRetries,
                retryCount,
                exception.GetType().Name,
                exception.Message);
            return;
        }

        // All retries exhausted — delegate to the registered error handler.
        // Returning normally means the global handler consumed the error and
        // the job continues; throwing means the configured action was applied
        // (abort job / stop all jobs / close application) and this job must stop.
        try
        {
            _context.ServiceProvider.GetService<IJobErrorHandler>()?.HandleError(_context, error);
        }
        catch
        {
            _abortedByError = true;
        }
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

    public void Dispose() => Shutdown();
}
