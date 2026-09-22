namespace Sa.Utils.WorkQueue;

using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Channels;

public sealed class SaWorkQueue<TInput> : ISaWorkQueue<TInput>
{
    private sealed record WorkItem(TInput Input, CancellationToken CancellationToken);

    private enum QueueState
    {
        Active = 0,
        Shutdown = 1,
        Disposed = 2
    }

    private readonly Channel<WorkItem> _queue;

    private readonly Lock _wiSync = new();
    private readonly Lock _readersSync = new();

    private readonly ILogger? _logger;
    private readonly ISaWork<TInput> _processor;

    private readonly Action<TInput, SaWorkStatus, Exception?>? _statusChanged;
    private readonly Func<TInput, Exception, SaExecutionErrorStrategy> _handleItemFaulted;
    private readonly Func<TInput, string> _getItemDisplayName;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;

    // Current concurrency limit, set by the user.
    private volatile int _concurrency;
    // 0: Active, 1: Shutdown, 2: Disposed
    private QueueState _state;

    private volatile Exception? _shutdownError = null;

    private volatile int _taskCount;
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Readers cancelled PLANNED (limit decrease). Only under _readersSync.
    private readonly HashSet<CancellationTokenSource> _intentionalRemovals = [];

    // Readers cancelled via ForceCancelReaders. Only under _readersSync.
    private readonly HashSet<CancellationTokenSource> _forceCancelled = [];

    // Cancelled but not yet completed (reap). Only under _readersSync.
    // Live readers = _ctsReaders.Count - _pendingRemovals.
    private int _pendingRemovals;

    private readonly List<CancellationTokenSource> _ctsReaders = [];
    // Work tokens per reader, kept in parallel order with _ctsReaders.
    // In Soft mode the work token is linked to this CTS (not the loop CTS),
    // so a planned removal / pause does not interrupt the in-flight item;
    // Force cancel uses it to escalate to a hard cancel.
    private readonly List<CancellationTokenSource> _ctsWorks = [];
    private readonly List<Task> _taskReaders = [];

    private readonly SaReaderCancellationOrder _cancellationOrder;
    private readonly SaReaderCancelMode _cancelMode;
    private readonly SaEnqueueStrategy _enqueueStrategy;
    private int _lastRemovedIndex = -1; // For RoundRobin

    private readonly TimeSpan _shutdownTimeout;

    // Single-flight dispose: 0 = not started, 1 = in progress. The winner
    // runs the shutdown and releases _shutdownCts; a concurrent Dispose/
    // DisposeAsync loser waits for the winner instead of racing it (the CTS
    // would otherwise be disposed out from under a still-running shutdown).
    private int _disposeStarted;
    private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SaWorkQueue(SaWorkQueueOptions<TInput> options, ILogger<SaWorkQueue<TInput>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options.Processor);

        _logger = logger;
        _processor = options.Processor;
        _statusChanged = options.StatusChanged;

        _maxConcurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency.Value : Environment.ProcessorCount;
        _concurrency = Math.Clamp(options.ConcurrencyLimit ?? Environment.ProcessorCount, 0, _maxConcurrency);
        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;

        _cancellationOrder = options.ReaderCancellationOrder;
        _cancelMode = options.ReaderCancelMode;
        _enqueueStrategy = options.EnqueueStrategy;
        _getItemDisplayName = options.GetItemDisplayName ?? (item => $"{item}");
        _handleItemFaulted = options.HandleItemFaulted ?? ((_, _) => SaExecutionErrorStrategy.ShutdownQueue);
        _shutdownTimeout = options.ShutdownTimeout ?? TimeSpan.FromSeconds(30);

        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = BoundedChannelFullMode.Wait
        });

        SpawnReaders(_concurrency);
    }

    public bool IsEnabled => _state == QueueState.Active;

    public int QueueTasks => _taskCount;

    public bool IsIdle() => _taskCount == 0;

    public int MaxConcurrency => _maxConcurrency;
    public int QueueCapacity => _queueCapacity;

    public int AvailableCapacity
        => _queueCapacity - (_queue.Reader.CanCount ? _queue.Reader.Count : 0);

    public Exception? ShutdownError => _shutdownError;

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            var newLimit = Math.Clamp(value, 0, _maxConcurrency);

            // The entire limit-change and reader-adjustment process is atomic and
            // protected from races with dying readers.
            lock (_readersSync)
            {
                _concurrency = newLimit;

                if (IsEnabled)
                {
                    var live = _ctsReaders.Count - _pendingRemovals;
                    var delta = newLimit - live;

                    if (delta > 0)
                    {
                        for (var i = 0; i < delta; i++)
                        {
                            StartReaderUnderLock();
                        }
                    }
                    else if (delta < 0)
                    {
                        CancelReadersUnderLock(-delta);
                    }
                }
            }
        }
    }

    public async ValueTask<bool> Enqueue(TInput input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
        if (!IsEnabled) ThrowHelper.QueueStopped();

        var wi = new WorkItem(input, cancellationToken);

        if (_enqueueStrategy == SaEnqueueStrategy.Wait)
        {
            MarkActive();
            await WriteAsyncBalanced(wi, cancellationToken).ConfigureAwait(false);
            return true;
        }

        MarkActive();

        if (_queue.Writer.TryWrite(wi))
        {
            return true;
        }

        MarkInactive();

        // Shutdown raced with us and completed the writer: report
        // "stopped" rather than "full".
        if (!IsEnabled)
        {
            ThrowHelper.QueueStopped();
        }

        if (_enqueueStrategy == SaEnqueueStrategy.Throw)
        {
            throw new SaWorkQueueFullException(_queueCapacity, _queue.Reader.Count, _getItemDisplayName(input));
        }

        OnStatusChanged(input, SaWorkStatus.Skipped);
        return false;
    }

    /// <summary>
    /// Waits for space in the bounded channel and writes <paramref name="wi"/>.
    /// The caller must have already marked the item active; on failure the pending
    /// count is balanced with <see cref="MarkInactive"/> and the exception is rethrown
    /// (writer-completed faults are mapped to "queue stopped").
    /// </summary>
    private async ValueTask<bool> WriteAsyncBalanced(WorkItem wi, CancellationToken cancellationToken)
    {
        try
        {
            await _queue.Writer.WriteAsync(wi, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            MarkInactive();

            if (ex is ChannelClosedException or IOException or InvalidOperationException)
            {
                ThrowHelper.QueueStopped();
            }

            throw;
        }
    }

    public async ValueTask<int> EnqueueMany(IEnumerable<TInput> inputs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
        if (!IsEnabled) ThrowHelper.QueueStopped();

        var accepted = 0;
        var attempted = 0;

        foreach (var input in inputs)
        {
            attempted++;
            var wi = new WorkItem(input, cancellationToken);

            MarkActive();

            bool ok;
            if (_enqueueStrategy == SaEnqueueStrategy.Wait)
            {
                ok = await WriteAsyncBalanced(wi, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                ok = _queue.Writer.TryWrite(wi);
            }

            if (ok)
            {
                accepted++;
                continue;
            }

            // Only reachable for the non-Wait path: WriteAsyncBalanced either
            // returned true or already balanced the count and rethrew.
            MarkInactive();

            // Shutdown raced with us and completed the writer: report
            // "stopped" rather than "full".
            if (!IsEnabled)
            {
                ThrowHelper.QueueStopped();
            }

            if (_enqueueStrategy == SaEnqueueStrategy.Throw)
            {
                throw new SaWorkQueueFullException(_queueCapacity, _queue.Reader.Count, accepted, attempted);
            }

            OnStatusChanged(input, SaWorkStatus.Skipped);
        }

        return accepted;
    }

    public bool TryEnqueue(TInput input)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
        if (!IsEnabled) ThrowHelper.QueueStopped();

        var wi = new WorkItem(input, default);

        MarkActive();

        if (_queue.Writer.TryWrite(wi))
        {
            return true;
        }

        MarkInactive();

        // Shutdown raced with us and completed the writer: report
        // "stopped" rather than "full".
        if (!IsEnabled)
        {
            ThrowHelper.QueueStopped();
        }

        OnStatusChanged(input, SaWorkStatus.Skipped);
        return false;
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);

        while (true)
        {
            TaskCompletionSource tcs;
            lock (_wiSync)
            {
                if (_taskCount == 0) return;
                tcs = _idleTcs;
            }

            // Work is still queued but the effective concurrency is 0: no readers
            // exist, so the queue can never drain on its own. Skip the wait and
            // return immediately instead of blocking forever for an idle state
            // that cannot be reached while paused.
            if (_concurrency == 0)
            {
                return;
            }

            try
            {
                await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Idle wait was cancelled by the user.", ex, cancellationToken);
                }
            }
        }
    }

    public void ForceCancelReaders()
    {
        if (!IsEnabled) return;

        var tasks = CancelAndTrackReaders();

        if (tasks.Length > 0)
        {
            // Bounded wait: a processor that ignores cancellation
            // must not block the calling thread indefinitely.
            Task.WaitAll(tasks, _shutdownTimeout);
        }

        DrainAndResetIdle();
    }

    public async Task ForceCancelReadersAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var tasks = CancelAndTrackReaders();

        if (tasks.Length > 0)
        {
            if (timeout is { } t)
                await Task.WhenAll(tasks).WaitAsync(t, ct).ConfigureAwait(false);
            else
                await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        DrainAndResetIdle();
    }

    /// <summary>
    /// Cancels every live reader, records each cancellation for accounting,
    /// and returns the tasks of the readers that were cancelled. Shared by
    /// <see cref="ForceCancelReaders"/> and <see cref="ForceCancelReadersAsync"/>.
    /// Must only be called when the queue is enabled.
    /// </summary>
    private Task[] CancelAndTrackReaders()
    {
        Task[] tasks;
        List<(CancellationTokenSource cts, CancellationTokenSource work)> readers;

        lock (_readersSync)
        {
            readers = [.. _ctsReaders
                .Select((cts, i) => (cts: cts, work: _ctsWorks[i]))
                .Where(p => !p.cts.IsCancellationRequested)];
            tasks = [.. _taskReaders];
        }

        foreach (var (cts, work) in readers)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                continue; // already reaped by RemoveReader
            }

            // Force cancel is always hard, even in Soft mode: also cancel the
            // per-reader work CTS so an in-flight item is interrupted
            // immediately instead of finishing.
            if (_cancelMode == SaReaderCancelMode.Soft)
            {
                try
                {
                    work.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already reaped by RemoveReader
                }
            }

            // Request cancellation and record it in the same critical section,
            // checking the reader is still tracked so a CTS that a concurrent
            // RemoveReader already reaped is not double-counted in
            // _pendingRemovals nor re-added to _forceCancelled.
            lock (_readersSync)
            {
                if (!_ctsReaders.Contains(cts))
                    continue;
                _forceCancelled.Add(cts);
                _pendingRemovals++;
            }
        }

        return tasks;
    }

    /// <summary>
    /// Drains any items left in the channel, resets <c>_taskCount</c> to zero,
    /// and resolves the idle TCS. Used by emergency stop so that
    /// <see cref="IsIdle"/> and <see cref="QueueTasks"/> stay honest.
    /// </summary>
    private void DrainAndResetIdle()
    {
        while (_queue.Reader.TryRead(out var item))
        {
            // An item with a cancelled caller token already received
            // Aborted status — don't report it again.
            if (item.CancellationToken.IsCancellationRequested)
            {
                continue;
            }

            // A fresh exception per item so observers can distinguish which
            // work item was dropped by shutdown (the previous shared singleton
            // made every dropped item reference-identical).
            OnStatusChanged(item.Input, SaWorkStatus.Faulted, ThrowHelper.QueueShutdownException());
            MarkInactive();
        }

        lock (_wiSync)
        {
            if (_taskCount > 0)
            {
                _taskCount = 0;
                _idleTcs.TrySetResult();
            }
        }
    }

    private void SpawnReaders(int count)
    {
        lock (_readersSync)
        {
            for (var i = 0; i < count; i++)
            {
                StartReaderUnderLock();
            }
        }
    }

    private void StartReaderUnderLock()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var ctsWork = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        var task = ReaderLoopAsync(cts, ctsWork);

        _ctsReaders.Add(cts);
        _ctsWorks.Add(ctsWork);
        _taskReaders.Add(task);
    }

    // Called ONLY under lock (_readersSync).
    // Selects readers only among live (non-cancelled) ones and marks
    // planned removal on a specific CTS — not a global counter.
    private void CancelReadersUnderLock(int toCancel)
    {
        var liveReaders = new List<CancellationTokenSource>();
        foreach (var cts in _ctsReaders)
        {
            if (!cts.IsCancellationRequested)
            {
                liveReaders.Add(cts);
            }
        }

        var total = liveReaders.Count;
        if (total == 0) return;

        toCancel = Math.Min(toCancel, total);

        int[] indices = _cancellationOrder switch
        {
            SaReaderCancellationOrder.Lifo => [.. Enumerable.Range(total - toCancel, toCancel)],
            SaReaderCancellationOrder.Fifo => [.. Enumerable.Range(0, toCancel)],
            SaReaderCancellationOrder.RoundRobin => RoundRobinIndices(total, toCancel),
            SaReaderCancellationOrder.Random => RandomIndices(total, toCancel),
            _ => [.. Enumerable.Range(total - toCancel, toCancel)]
        };

        foreach (var idx in indices)
        {
            var cts = liveReaders[idx];
            _intentionalRemovals.Add(cts);
            _pendingRemovals++;
            cts.Cancel();
        }
    }

    private int[] RoundRobinIndices(int totalCount, int toCancel)
    {
        var start = (_lastRemovedIndex + 1) % totalCount;
        var indices = new int[toCancel];
        for (var i = 0; i < toCancel; i++)
        {
            indices[i] = (start + i) % totalCount;
        }

        _lastRemovedIndex = (start + toCancel - 1) % totalCount;
        return indices;
    }

    private static int[] RandomIndices(int totalCount, int toCancel)
    {
        // Pick `toCancel` distinct indices uniformly at random. Using a set
        // keeps the intent explicit (choose unique indices) and avoids shuffling
        // the full range when only a handful are needed.
        var indices = new HashSet<int>();
        var rng = Random.Shared;
        while (indices.Count < toCancel)
        {
            indices.Add(rng.Next(totalCount));
        }

        return [.. indices];
    }

    private async Task ReaderLoopAsync(CancellationTokenSource cts, CancellationTokenSource ctsWork)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                WorkItem item;
                try
                {
                    item = await _queue.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                bool isContinue = false;

                // Soft mode: the work token is linked to the per-reader work CTS
                // (not the loop CTS), so a planned removal / pause does not
                // interrupt the in-flight item. Both CTSs are linked to
                // _shutdownCts, so shutdown stays hard in every mode.
                var workParent = _cancelMode == SaReaderCancelMode.Soft ? ctsWork : cts;
                using var ctsExec = CancellationTokenSource.CreateLinkedTokenSource(
                    workParent.Token, item.CancellationToken);

                isContinue = await ExecuteItemAsync(item, ctsExec.Token).ConfigureAwait(false);
                if (!isContinue) break;
            }
        }
        catch (ObjectDisposedException)
        {
            // The shutdown CTS was disposed out from under this reader
            // (Dispose ran after the shutdown wait timed out and this reader
            // was still alive). Expected late exit: no extra shutdown, no
            // scary "ReaderTask error" log — RemoveReader in finally cleans up.
        }
        catch (Exception ex)
        {
            SaWorkQueueLogMessages.LogReaderError(_logger, ex);
            _ = ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            RemoveReader(cts);
        }
    }

    private void RemoveReader(CancellationTokenSource cts)
    {
        bool intentional;
        bool tracked;
        CancellationTokenSource? work = null;
        lock (_readersSync)
        {
            var idx = _ctsReaders.IndexOf(cts);
            if (idx >= 0)
            {
                // All three lists are kept in parallel order by
                // StartReaderUnderLock; remove at the same index so completed
                // Tasks are not retained (previously _taskReaders grew without
                // bound -> memory leak).
                _ctsReaders.RemoveAt(idx);
                if (idx < _ctsWorks.Count)
                {
                    work = _ctsWorks[idx];
                    _ctsWorks.RemoveAt(idx);
                }
                if (idx < _taskReaders.Count)
                {
                    _taskReaders.RemoveAt(idx);
                }
            }

            // Planned removal (limit decrease): _concurrency is already
            // set by the setter, no need to decrement it again.
            intentional = _intentionalRemovals.Remove(cts);

            // Was this CTS's cancellation tracked (planned or ForceCancel)?
            tracked = intentional || _forceCancelled.Remove(cts);
            if (tracked)
            {
                _pendingRemovals--;
            }

            // Unplanned termination (StopReader, crash, ForceCancel):
            // pool capacity decreases; recovery is via manually
            // setting ConcurrencyLimit.
            if (!intentional && _concurrency > 0)
            {
                _concurrency--;
            }
        }

        cts.Dispose();
        work?.Dispose();

        if (IsEnabled && !intentional && !tracked)
        {
            SaWorkQueueLogMessages.LogReaderLost(_logger, _concurrency);
        }
    }

    private async Task<bool> ExecuteItemAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            OnStatusChanged(item.Input, SaWorkStatus.Running);
            await _processor.Execute(item.Input, ct).ConfigureAwait(false);
            OnStatusChanged(item.Input, SaWorkStatus.Completed);
            return true;
        }
        catch (OperationCanceledException ex) when (item.CancellationToken.IsCancellationRequested)
        {
            OnStatusChanged(item.Input, SaWorkStatus.Aborted);
            SaWorkQueueLogMessages.LogItemAborted(_logger, _getItemDisplayName(item.Input), ex);
            return true;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            OnStatusChanged(item.Input, SaWorkStatus.Cancelled);
            SaWorkQueueLogMessages.LogItemCancelled(_logger, _getItemDisplayName(item.Input), ex);
            return false;
        }
        catch (Exception ex)
        {
            var displayItem = _getItemDisplayName(item.Input);

            SaExecutionErrorStrategy errorStrategy = SaExecutionErrorStrategy.ShutdownQueue;
            try
            {
                OnStatusChanged(item.Input, SaWorkStatus.Faulted, ex);
                SaWorkQueueLogMessages.LogItemExecutionFailed(_logger, displayItem, ex);

                errorStrategy = _handleItemFaulted(item.Input, ex);
            }
            catch (Exception callbackEx)
            {
                SaWorkQueueLogMessages.LogItemHandlerFailed(_logger, displayItem, callbackEx);
            }

            return errorStrategy switch
            {
                SaExecutionErrorStrategy.Continue => true,
                SaExecutionErrorStrategy.StopReader => false,
                SaExecutionErrorStrategy.ShutdownQueue => HandleShutdownOnError(ex),
                _ => true
            };
        }
        finally
        {
            MarkInactive();
        }
    }

    private bool HandleShutdownOnError(Exception ex)
    {
        _shutdownError = ex;
        _ = ShutdownAsync().ConfigureAwait(false);
        return false;
    }

    private void MarkActive()
    {
        TaskCompletionSource? tcs = null;
        lock (_wiSync)
        {
            if (_taskCount++ == 0)
            {
                tcs = _idleTcs;
                _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        tcs?.TrySetResult();
    }

    private void MarkInactive()
    {
        TaskCompletionSource? tcs = null;
        lock (_wiSync)
        {
            if (_taskCount > 0)
            {
                _taskCount--;
                if (_taskCount == 0)
                {
                    tcs = _idleTcs;
                }
            }
        }
        tcs?.TrySetResult();
    }

    private void OnStatusChanged(TInput item, SaWorkStatus status, Exception? error = null)
    {
        var callback = _statusChanged;
        if (callback is null) return;

        Exception? handlerEx = null;
        try
        {
            // Called without lock: a slow handler must not block
            // other readers. Callback instances may be invoked
            // concurrently from different readers.
            callback(item, status, error);
        }
        catch (Exception ex)
        {
            handlerEx = ex;
        }

        if (handlerEx is not null)
        {
            SaWorkQueueLogMessages.LogItemHandlerFailed(_logger, _getItemDisplayName(item), handlerEx);
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.CompareExchange(ref _state, QueueState.Shutdown, QueueState.Active) != QueueState.Active)
            return;

        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _queue.Writer.TryComplete();
            await WaitForReadersToCompleteAsync().ConfigureAwait(false);
            DrainAndResetIdle();
        }
        catch (Exception ex)
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
        }
    }

    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _state, QueueState.Shutdown, QueueState.Active) != QueueState.Active)
            return;

        try
        {
            _shutdownCts.Cancel();
            _queue.Writer.TryComplete();
            WaitForReadersToComplete();
            DrainAndResetIdle();
        }
        catch (Exception ex)
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ex);
        }
    }

    /// <summary>
    /// Waits for all live reader tasks to finish, bounded by the
    /// <see cref="_shutdownTimeout"/> (logs and continues if it elapses first).
    /// Non-blocking: uses <see cref="Task.WaitAsync(TimeSpan)"/>.
    /// </summary>
    private async Task WaitForReadersToCompleteAsync()
    {
        Task[] tasks;
        lock (_readersSync)
        {
            tasks = [.. _taskReaders];
        }

        if (tasks.Length == 0)
        {
            return;
        }

        // Bounded wait: a processor that ignores cancellation must not block
        // shutdown (and therefore DisposeAsync) indefinitely.
        try
        {
            await Task.WhenAll(tasks).WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ThrowHelper.ReadersTimeout(asynchronous: true, _shutdownTimeout.TotalSeconds));
        }
    }

    /// <summary>
    /// Blocking counterpart of <see cref="WaitForReadersToCompleteAsync"/>. Used by
    /// the synchronous <see cref="Shutdown"/>; readers that ignore cancellation are
    /// bounded by the <see cref="_shutdownTimeout"/>.
    /// </summary>
    private void WaitForReadersToComplete()
    {
        Task[] tasks;
        lock (_readersSync)
        {
            tasks = [.. _taskReaders];
        }

        if (tasks.Length == 0)
        {
            return;
        }

        if (!Task.WaitAll(tasks, _shutdownTimeout))
        {
            SaWorkQueueLogMessages.LogShutdownError(_logger, ThrowHelper.ReadersTimeout(asynchronous: false, _shutdownTimeout.TotalSeconds));
        }
    }

    private void CompleteDispose()
    {
        if (Interlocked.Exchange(ref _state, QueueState.Disposed) != QueueState.Disposed)
        {
            _shutdownCts.Dispose();
        }
    }

    public void Dispose()
        => RunDispose(() => Shutdown());

    public async ValueTask DisposeAsync()
    {
        await RunDisposeAsync(async () =>
        {
            if (IsEnabled)
            {
                await ShutdownAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-flight dispose: the first caller becomes the "winner", runs
    /// <paramref name="shutdown"/>, and tears down shared state; any concurrent
    /// loser waits for the winner instead of racing it.
    /// </summary>
    /// <remarks>
    /// The winner must not be beaten to the teardown by a concurrent call: the
    /// <c>_shutdownCts</c> must not be disposed out from under a still-running
    /// shutdown, and a loser must never observe a half-disposed queue.
    /// </remarks>
    private void RunDispose(Action shutdown)
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            // A concurrent Dispose/DisposeAsync is already shutting down and
            // will release _shutdownCts. Wait for it to finish instead of
            // racing it.
            _disposeCompleted.Task.Wait();
            return;
        }

        try
        {
            shutdown();
        }
        finally
        {
            CompleteDispose();
            _disposeCompleted.TrySetResult();
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="RunDispose(Action)"/>. Lets the
    /// loser await the winner rather than blocking the calling thread.
    /// </summary>
    private async Task RunDisposeAsync(Func<Task> shutdown)
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            await _disposeCompleted.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await shutdown().ConfigureAwait(false);
        }
        finally
        {
            CompleteDispose();
            _disposeCompleted.TrySetResult();
        }
    }

}
