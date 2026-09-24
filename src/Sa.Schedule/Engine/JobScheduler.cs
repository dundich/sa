using Microsoft.Extensions.Primitives;
using Sa.Utils.WorkQueue;

namespace Sa.Schedule.Engine;

internal sealed class JobScheduler : IJobScheduler
{
    private readonly static IChangeToken NoneChangeToken
        = new CancellationChangeToken(CancellationToken.None);

    private readonly Lock _lock = new();

    private CancellationTokenSource _stoppingTokenSource = new();

    private bool? _started = false;

    private bool _disposed;

    // The user-requested concurrency limit (initially the configured one).
    // The queue itself tracks the effective limit; this copy is used to
    // restore readers after they are force-cancelled.
    private volatile int _limit;

    private readonly SaWorkQueue<IJobController> _queue;

    private readonly Func<int, IJobController> _createController;

    private readonly IJobRunner _runner;

    private IReadOnlyList<IJobController> _jobControllers = [];

    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(30);

    // How long Stop waits for running iterations to finish before giving up;
    // the same value bounds the queue's wait for readers on shutdown/force-cancel.
    private readonly TimeSpan _shutdownTimeout;


    public JobScheduler(
        IJobSettings settings,
        IJobRunner runner,
        Func<int, IJobController> createController)
    {

        _runner = runner;
        _createController = createController;

        JobId = settings.JobId;

        _shutdownTimeout = settings.Properties.ShutdownTimeout ?? DefaultShutdownTimeout;

        int maxConcurrency = settings.Properties.MaxConcurrency.GetValueOrDefault(1);
        maxConcurrency = Math.Clamp(maxConcurrency, 1, int.MaxValue);

        _limit = Math.Clamp(settings.Properties.ConcurrencyLimit.GetValueOrDefault(1), 0, maxConcurrency);

        _queue = new SaWorkQueue<IJobController>(
            SaWorkQueueOptions<IJobController>.Create(RunJob)
            .WithQueueCapacity(maxConcurrency)
            .WithMaxConcurrency(maxConcurrency)
            .WithConcurrencyLimit(_limit)
            .WithSingleWriter(true)
            .WithShutdownTimeout(_shutdownTimeout)
            // An unexpected fault must not kill the queue (the default
            // ShutdownQueue is irreversible); a dead slot is dropped instead
            // and the next Start() restores the reader pool.
            .WithHandleItemFaulted((_, _) => SaExecutionErrorStrategy.StopReader)
        );
    }

    public Guid JobId { get; }

    public int ConcurrencyLimit
    {
        get => _queue.ConcurrencyLimit;
        set
        {
            _limit = Math.Clamp(value, 0, _queue.MaxConcurrency);
            _queue.ConcurrencyLimit = _limit;
            RefreshConcurrency();
        }
    }

    public bool IsStarted
    {
        get
        {
            lock (_lock)
            {
                return !_disposed && _started.GetValueOrDefault();
            }
        }
    }


    /// <summary>
    /// The number of tasks currently in the queue buffer
    /// (up to <see cref="Sa.Utils.WorkQueue.ISaWorkQueue{TInput}.MaxConcurrency"/>
    /// while the job is running, 0 otherwise).
    /// </summary>
    public int QueueTasks => _queue.QueueTasks;

    public IChangeToken StartChangeToken()
    {
        lock (_lock)
        {
            if (_disposed) return NoneChangeToken;
            return new CancellationChangeToken(_stoppingTokenSource.Token);
        }
    }


    public async Task<bool> Start(CancellationToken cancellationToken)
    {
        CancellationToken stoppingToken;

        lock (_lock)
        {
            if (_disposed || (_started == null || _started == true))
            {
                return false;
            }

            _started = null;

            _stoppingTokenSource.Cancel();
            _stoppingTokenSource.Dispose();

            _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            stoppingToken = _stoppingTokenSource.Token;
        }

        List<IJobController> controllers = new(capacity: _queue.MaxConcurrency);

        bool success = false;

        try
        {
            // Re-spawn readers lost by a previous abort/force-cancel
            // (setting the same value is a no-op when the pool is healthy).
            _queue.ConcurrencyLimit = _limit;

            await _queue.WaitForIdleAsync(cancellationToken);

            for (var i = 0; i < _queue.MaxConcurrency; i++)
            {
                IJobController controller = _createController(i);
                controller.Pause();
                controllers.Add(controller);
                await _queue.Enqueue(controller, stoppingToken);
            }

            success = true;
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested during startup
        }
        catch (ObjectDisposedException)
        {
            // The scheduler (or its queue) was disposed while starting
        }
        finally
        {
            lock (_lock)
            {
                _started = success;
                _jobControllers = controllers;
            }

            if (!success)
            {
                // Dispose already-created (possibly enqueued) controllers
                foreach (var controller in controllers)
                {
                    controller.Shutdown();
                }
            }
        }

        RefreshConcurrency();

        return success;
    }

    private void RefreshConcurrency()
    {
        IReadOnlyList<IJobController> controllers;

        lock (_lock)
        {
            controllers = _jobControllers;
        }

        int limit = _queue.ConcurrencyLimit;

        for (int i = 0; i < controllers.Count; i++)
        {
            if (i < limit)
            {
                controllers[i].Resume();
            }
            else
            {
                controllers[i].Pause();
            }
        }
    }

    /// <summary>
    /// Processes a job slot on a queue reader: runs the slot's loop until it exits.
    /// </summary>
    private async Task RunJob(IJobController controller, CancellationToken ct)
    {
        bool aborted = await _runner.Run(controller, ct);

        if (aborted)
        {
            AbortJob();
        }
    }

    /// <summary>
    /// Stops the whole job after its error handling requested an abort:
    /// interrupts the running iterations, force-cancels all readers, and marks
    /// the job as stopped. The queue stays active, so the job can be started
    /// again with <see cref="Start"/>.
    /// </summary>
    private void AbortJob()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _started = false;
        }

        try
        {
            _stoppingTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        // Fire and forget: this runs on a queue reader thread, and
        // ForceCancelReadersAsync waits for every reader — including this one —
        // so it must not be awaited here.
        _ = Task.Run(async () =>
        {
            try
            {
                await _queue.ForceCancelReadersAsync();
            }
            catch
            {
                // The queue may already be disposed
            }
        });
    }

    public async Task Stop()
    {
        CancellationTokenSource stoppingTokenSource;

        lock (_lock)
        {
            if (_disposed || !_started.GetValueOrDefault()) return;

            stoppingTokenSource = _stoppingTokenSource;
            stoppingTokenSource.Cancel();
            _started = false;
        }

        using var timeoutCts = new CancellationTokenSource(_shutdownTimeout);

        try
        {
            await _queue.WaitForIdleAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation — jobs didn't finish within the shutdown timeout
        }
    }

    public void Dispose()
    {
        CancellationTokenSource ctsStopping;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ctsStopping = _stoppingTokenSource;
        }

        try
        {
            ctsStopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        ShutdownControllers();

        _queue.Dispose();

        ctsStopping.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource ctsStopping;

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ctsStopping = _stoppingTokenSource;
        }

        // Cancelling wakes slots parked on the pause gate and interrupts
        // running iterations; without it the controllers would never be
        // released (and their DI scopes would leak).
        try
        {
            await ctsStopping.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }

        ShutdownControllers();

        await _queue.DisposeAsync();

        ctsStopping.Dispose();
    }

    private void ShutdownControllers()
    {
        IReadOnlyList<IJobController> controllers;

        lock (_lock)
        {
            controllers = _jobControllers;
        }

        foreach (var controller in controllers)
        {
            // Idempotent — the queue shutdown may have already done it.
            controller.Shutdown();
        }
    }
}
