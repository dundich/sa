using Microsoft.Extensions.Logging;
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

    // Set when a RunOnce job ran its single iteration to a natural end (no
    // error abort). Per the RunOnce contract ("stop permanently") the job must
    // not start again, so Start() consults this. Read/written under _lock.
    private bool _runOnceCompleted;

    // Set when Stop() lands while a Start() is still in flight. Start()'s
    // completion consumes the flag and treats the start as failed, so the stop
    // request is never silently dropped.
    private bool _stopRequested;

    // Set when the job's loop ended (RunOnce completion, unsatisfiable schedule,
    // error abort) while a Start() is still spawning its slots. Start() consumes
    // them: the start itself still succeeded, but there is nothing running to
    // report — IsStarted must end up false, not true over a dead job.
    private bool _jobEndedDuringStart;

    // Subset of the above: the loop ended because a slot *faulted* (the job could
    // not be constructed, a custom timing threw, ...). That is a failed start, so
    // Start() also reports false.
    private bool _faultedDuringStart;

    private readonly ILogger<JobScheduler>? _logger;

    // The user-requested concurrency limit (initially the configured one). The
    // reader pool itself stays at MaxConcurrency for the scheduler's lifetime;
    // this value is the single source of truth for how many slots are active,
    // applied by pausing/resuming the controller gates in RefreshConcurrency.
    private volatile int _limit;

    private readonly SaWorkQueue<IJobController> _queue;

    private readonly Func<int, IJobController> _createController;

    private readonly IJobRunner _runner;

    // Snapshot of settings.Properties.IsRunOnce (the schedule config that could
    // change it is applied before a scheduler is created).
    private readonly bool _isRunOnce;

    private IReadOnlyList<IJobController> _jobControllers = [];

    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(30);

    // How long Stop waits for running iterations to finish before giving up;
    // the same value bounds the queue's wait for readers on shutdown/force-cancel.
    private readonly TimeSpan _shutdownTimeout;


    public JobScheduler(
        IJobSettings settings,
        IJobRunner runner,
        Func<int, IJobController> createController,
        ILogger<JobScheduler>? logger = null)
    {

        _runner = runner;
        _createController = createController;
        _logger = logger;

        JobId = settings.JobId;

        // Config overlay is applied before a scheduler is ever created, so the
        // immutable snapshot below is final for the scheduler's lifetime.
        _isRunOnce = settings.Properties.IsRunOnce == true;

        _shutdownTimeout = settings.Properties.ShutdownTimeout ?? DefaultShutdownTimeout;

        // MaxConcurrency is the upper bound of the pre-allocated slot pool. It
        // defaults to the concurrency limit when only the latter is configured
        // (the documented contract — see IJobProperties.MaxConcurrency), and
        // both default to 1 when neither is set.
        int maxConcurrency = settings.Properties.MaxConcurrency
            ?? settings.Properties.ConcurrencyLimit.GetValueOrDefault(1);

        maxConcurrency = Math.Clamp(maxConcurrency, 1, int.MaxValue);

        _limit = Math.Clamp(settings.Properties.ConcurrencyLimit.GetValueOrDefault(1), 0, maxConcurrency);

        _queue = new SaWorkQueue<IJobController>(
            SaWorkQueueOptions<IJobController>.Create(RunJob)
            .WithQueueCapacity(maxConcurrency)
            .WithMaxConcurrency(maxConcurrency)
            // The reader pool stays at MaxConcurrency; the *effective* limit is
            // applied by pausing/resuming the slot gates (see RefreshConcurrency).
            // Letting the queue shrink instead hard-cancels a reader's in-flight
            // item — and an item here is a job's whole loop — so every slot above
            // the new limit would be gone for good: raising the limit again could
            // never bring it back without a full Stop+Start.
            .WithConcurrencyLimit(maxConcurrency)
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
        get => _limit;
        set
        {
            _limit = Math.Clamp(value, 0, _queue.MaxConcurrency);
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
            // The token signals "the running job stopped". When nothing is
            // running (or a start is still in flight) there is no pending
            // transition, so the returned token must not fire: an already-fired
            // token would claim a change that happened before the caller
            // subscribed. Poll IsStarted to observe a later start — this is a
            // change token, not a state query.
            if (_disposed || !_started.GetValueOrDefault()) return NoneChangeToken;

            return new CancellationChangeToken(_stoppingTokenSource.Token);
        }
    }


    public async Task<bool> Start(CancellationToken cancellationToken)
    {
        CancellationToken stoppingToken;
        CancellationTokenSource? previousSource = null;

        lock (_lock)
        {
            // A naturally completed RunOnce job stays stopped forever — its
            // Start() is refused, not silently re-run.
            if (_disposed || _runOnceCompleted || _started == null || _started == true)
            {
                return false;
            }

            _started = null;

            previousSource = _stoppingTokenSource;
            _stoppingTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            stoppingToken = _stoppingTokenSource.Token;
        }

        // Cancel and dispose outside the lock: cancellation callbacks run on this
        // thread. (Stop() cancels under its lock on purpose — see the note there.)
        if (previousSource is not null)
        {
            try
            {
                previousSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }

            previousSource.Dispose();
        }

        List<IJobController> controllers = new(capacity: _queue.MaxConcurrency);

        bool success = false;

        try
        {
            // Re-spawn readers lost by a previous abort/force-cancel
            // (setting the same value is a no-op when the pool is healthy).
            _queue.ConcurrencyLimit = _queue.MaxConcurrency;

            // Bounded: a previous run's iteration may ignore cancellation (Stop only
            // waits out the shutdown timeout), and an unbounded wait here would hang
            // Restart()/Start() forever. A timeout fails this start instead.
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleCts.CancelAfter(_shutdownTimeout);

            await _queue.WaitForIdleAsync(cancellationToken: idleCts.Token);

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
            // Shutdown requested during startup — or the previous run did not drain
            // within the shutdown timeout.
        }
        catch (ObjectDisposedException)
        {
            // The scheduler (or its queue) was disposed while starting
        }
        finally
        {
            lock (_lock)
            {
                // A Stop() that landed while the readers were being spawned wins:
                // consume the request here and treat the start as failed, so the
                // job does not run (the request Stop made was not a no-op).
                if (success && _stopRequested)
                {
                    success = false;
                }

                // The job already ended (or faulted) while the slots were being
                // spawned. A fault is a failed start; a natural completion (immediate
                // RunOnce, unsatisfiable schedule) did start the job — it just must
                // not be reported as running.
                bool endedDuringStart = _jobEndedDuringStart;
                if (_faultedDuringStart)
                {
                    success = false;
                }

                _stopRequested = false;
                _jobEndedDuringStart = false;
                _faultedDuringStart = false;

                _started = success && !endedDuringStart;

                // Keep the previous list on a failed start: those controllers (and their
                // DI scopes) still need to be reachable by Dispose. The controllers this
                // failed attempt created are shut down by the failure branch below.
                if (success)
                {
                    _jobControllers = controllers;
                }
            }

            if (!success)
            {
                // Cancel the token this start created first, so any loop that
                // already began unwinds on its next await, then dispose the
                // already-created (possibly enqueued) controllers.
                try
                {
                    _stoppingTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // ignore
                }

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

        // The requested limit — not the queue's, which is pinned at MaxConcurrency
        // so the default reader pool is never torn down by a limit change.
        int limit = _limit;

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
        JobRunResult result;

        try
        {
            result = await _runner.Run(controller, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A normal stop: the token was cancelled while the slot was still waiting
            // on its initial delay or pause gate. No fault, no teardown to run.
            return;
        }
        catch (Exception ex)
        {
            // A fault outside the iteration loop (the job could not be constructed, a
            // custom IJobTiming threw, ...) must not be left to the queue's
            // faulted-item strategy alone: that drops the reader but keeps IsStarted
            // true over a dead slot. The controller is already shut down by the
            // runner's finally, so only the scheduler state is left to fix.
            if (_logger is not null)
            {
                JobSchedulerLog.LogSlotFaulted(_logger, JobId, controller.Index, ex);
            }

            AbortJob(faulted: true);
            return;
        }

        switch (result)
        {
            case JobRunResult.Finished:
                // The loop ended because there is no more work: the run-once job
                // completed, the schedule can never fire again (unsatisfiable cron),
                // or the error handling requested an abort. In every such case the
                // scheduler must reflect the stopped state and release the slot
                // readers — otherwise IsStarted would stay true over a dead job.
                if (!controller.AbortedByError && _isRunOnce)
                {
                    lock (_lock)
                    {
                        // A naturally completed run-once is consumed permanently:
                        // per the RunOnce contract the job must not start again.
                        _runOnceCompleted = true;
                    }
                }

                AbortJob();
                break;

            case JobRunResult.Faulted:
                // A fault is not a natural completion, so the RunOnce budget stays
                // intact and the job can be started again once the cause is fixed.
                // The slot is still torn down: the queue's faulted-item strategy alone
                // would leave IsStarted true over a dead slot.
                AbortJob(faulted: true);
                break;

            default:
                // Stopped: the caller cancelled the token. Normally Stop/Dispose already
                // moved the scheduler to the stopped state and this is a no-op. If it is
                // still marked started, the run ended because the caller's own token (for
                // example the host's startup token) was cancelled — reflect that in the
                // state as well.
                if (ct.IsCancellationRequested)
                {
                    AbortJob();
                }
                break;
        }
    }

    /// <summary>
    /// Stops the whole job after its loop ended on its own (run-once completed,
    /// unsatisfiable schedule) or after its error handling requested an abort:
    /// interrupts the running iterations, force-cancels all readers, and marks
    /// the job as stopped. The queue stays active, so the job can be started
    /// again with <see cref="Start"/> (unless it was a RunOnce job, which
    /// refuses to run again).
    /// </summary>
    /// <param name="faulted">
    /// <see langword="true"/> when the loop ended because a slot faulted rather than
    /// because it ran out of work. The distinction matters in two places: while a start
    /// is in flight (a fault fails that start, a normal completion does not) and for a
    /// RunOnce job (only a normal completion consumes its single run).
    /// </param>
    private void AbortJob(bool faulted = false)
    {
        CancellationTokenSource stoppingTokenSource;

        lock (_lock)
        {
            if (_disposed) return;

            // A Start() is still spawning slots: the brand-new source must not be
            // cancelled out from under it. Record the end instead — Start()'s
            // completion consumes it, so IsStarted never reports a dead job.
            if (_started is null)
            {
                _jobEndedDuringStart = true;
                if (faulted)
                {
                    _faultedDuringStart = true;
                }
                return;
            }

            // Only interrupt a job that is actually running. After a Stop there is
            // nothing left to tear down.
            if (!_started.GetValueOrDefault()) return;

            _started = false;

            // Read under the lock: Start() replaces this field, and cancelling
            // a stale read could hit a just-created source.
            stoppingTokenSource = _stoppingTokenSource;
        }

        try
        {
            stoppingTokenSource.Cancel();
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
        bool? started;

        lock (_lock)
        {
            if (_disposed) return;

            started = _started;

            if (started is null)
            {
                // A Start() is in flight — the job has not settled yet, but the
                // stop request must not be lost: Start()'s completion sees the
                // flag and cancels itself.
                _stopRequested = true;
                return;
            }

            if (!started.Value) return;

            // Cancelled under the lock on purpose: this closes the race with Dispose(),
            // which disposes the same source outside the lock — Cancel() on a disposed
            // source throws ObjectDisposedException. System.Threading.Lock is reentrant,
            // so a callback cannot deadlock on this lock from the same thread.
            stoppingTokenSource = _stoppingTokenSource;
            stoppingTokenSource.Cancel();
            _started = false;
        }

        using var timeoutCts = new CancellationTokenSource(_shutdownTimeout);

        try
        {
            await _queue.WaitForIdleAsync(cancellationToken: timeoutCts.Token);
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

        // Give in-flight iterations a bounded chance to observe the cancellation
        // and unwind on their own — their runners shut the controllers down as
        // they exit. Only the leftovers get their DI scope force-disposed by
        // ShutdownControllers below, so a cooperative iteration is never
        // used-after-dispose.
        using var timeoutCts = new CancellationTokenSource(_shutdownTimeout);

        try
        {
            _queue.WaitForIdleAsync(cancellationToken: timeoutCts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Timeout — this iteration ignores cancellation; force-dispose below
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

        // Drain before tearing the scopes down: after the cancellation lands the
        // runners unwind on their own and each disposes its controller (and DI
        // scope) in its finally. Waiting here (bounded by the shutdown timeout)
        // means a cooperative iteration finishes inside a live scope instead of
        // having it disposed from underneath it by ShutdownControllers.
        using var timeoutCts = new CancellationTokenSource(_shutdownTimeout);

        try
        {
            await _queue.WaitForIdleAsync(cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout — this iteration ignores cancellation; force-dispose below
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
