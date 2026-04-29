namespace Sa.Utils.WorkQueue;

using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Channels;

public sealed partial class SaWorkQueue<TInput> : ISaWorkQueue<TInput>
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
    private readonly Lock _cbSync = new();

    private readonly ILogger? _logger;
    private readonly ISaWork<TInput> _processor;

    private readonly Action<TInput, SaWorkStatus, Exception?>? _statusChanged;
    private readonly Func<TInput, Exception, SaExecutionErrorStrategy> _handleItemFaulted;
    private readonly Func<TInput, string> _getItemDisplayName;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;

    private volatile int _concurrency;
    /// <summary>
    /// 0: Active, 1: Shutdown, 2: Disposed
    /// </summary>
    private volatile QueueState _state;

    private volatile Exception? _shutdownError = null;

    // список ожидающих задач
    private volatile int _taskCount;
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile int _readerCount;

    private readonly List<CancellationTokenSource> _ctsReaders = [];
    private readonly List<Task> _taskReaders = [];

    private readonly SaReaderScalingStrategy _scalingStrategy;
    private int _lastRemovedIndex = -1; // Для RoundRobin


    public SaWorkQueue(SaWorkQueueOptions<TInput> options, ILogger<SaWorkQueue<TInput>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options.Processor);

        _logger = logger;
        _processor = options.Processor;

        _statusChanged = options.StatusChanged;

        _maxConcurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency.Value : Environment.ProcessorCount;
        _concurrency = Math.Clamp(options.ConcurrencyLimit ?? Environment.ProcessorCount, 0, _maxConcurrency);
        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;

        _scalingStrategy = options.ReaderScalingStrategy;
        _getItemDisplayName = options.GetItemDisplayName ?? (item => $"{item}");
        _handleItemFaulted = options.HandleItemFaulted ?? ((_, ex) => SaExecutionErrorStrategy.ShutdownQueue);

        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = options.FullMode
        });

        SpawnReaders(_concurrency);
    }

    public bool IsEnabled => _state == QueueState.Active;

    public int QueueTasks => _taskCount;

    public bool IsIdle() => QueueTasks == 0 && _queue.Reader.Count == 0;

    public int MaxConcurrency => _maxConcurrency;
    public int QueueCapacity => _queueCapacity;
    public Exception? ShutdownError => _shutdownError;

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            var newLimit = Math.Clamp(value, 0, _maxConcurrency);
            var oldLimit = Interlocked.Exchange(ref _concurrency, newLimit);
            if (newLimit != oldLimit && IsEnabled)
            {
                AdjustReaders(newLimit);
            }
        }
    }

    public async ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
        if (!IsEnabled) ThrowHelper.QueueStopped();

        var wi = new WorkItem(input, cancellationToken);
        MarkActive();

        try
        {
            await _queue.Writer.WriteAsync(wi, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkInactive();

            if (ex is ChannelClosedException)
            {
                ThrowHelper.QueueStopped();
            }
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token,
            cancellationToken);

        while (!IsIdle())
        {
            TaskCompletionSource tcs;
            lock (_wiSync) { tcs = _idleTcs; }
            try
            {
                await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
        }
    }


    /// <summary>
    /// Forcefully cancels all readers without waiting.
    /// Use only in emergency situations (e.g., when hanging).
    /// </summary>
    public void ForceCancelReaders()
    {
        if (!IsEnabled) return;

        CancellationTokenSource[] readers;

        lock (_readersSync)
        {
            readers = [.. _ctsReaders.Where(c => !c.IsCancellationRequested)];
        }

        foreach (var reader in readers)
        {
            try { reader.Cancel(); } catch (ObjectDisposedException) { /* ignored */ }
        }

        lock (_wiSync)
        {
            _idleTcs.TrySetResult();
        }
    }

    public async Task ForceCancelReadersAsync()
    {
        if (!IsEnabled) return;

        CancellationTokenSource[] readers;

        lock (_readersSync)
        {
            readers = [.. _ctsReaders.Where(c => !c.IsCancellationRequested)];
        }

        foreach (var reader in readers)
        {
            try { await reader.CancelAsync(); } catch (ObjectDisposedException) { /* ignored */ }
        }

        lock (_wiSync)
        {
            _idleTcs.TrySetResult();
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
        var task = ReaderLoopAsync(cts);

        _ctsReaders.Add(cts);
        _taskReaders.Add(task);
        _readerCount++;
    }

    private void AdjustReaders(int target)
    {
        if (!IsEnabled) return;

        lock (_readersSync)
        {
            int current = _readerCount;
            var delta = target - current;
            if (delta > 0)
            {
                for (int i = 0; i < delta; i++)
                {
                    StartReaderUnderLock();
                }
            }
            else if (delta < 0)
            {
                var total = _ctsReaders.Count;
                if (total == 0) return;
                var toCancel = Math.Min(-delta, total);

                var cancellationSources = SelectCancellationSources(toCancel, total);
                foreach (var cts in cancellationSources)
                {
                    cts.Cancel();
                }
            }
        }
    }

    private List<CancellationTokenSource> SelectCancellationSources(int toCancel, int total)
    {
        var list = new List<CancellationTokenSource>(toCancel);

        IEnumerable<int> indices = _scalingStrategy switch
        {
            SaReaderScalingStrategy.Lifo => Enumerable.Range(total - toCancel, toCancel),
            SaReaderScalingStrategy.Fifo => Enumerable.Range(0, toCancel),
            SaReaderScalingStrategy.RoundRobin => GetRoundRobinIndices(total, toCancel),
            SaReaderScalingStrategy.Random => GetRandomIndices(total, toCancel),
            _ => Enumerable.Range(total - toCancel, toCancel)
        };

        foreach (var idx in indices.Where(c => c < total))
        {
            list.Add(_ctsReaders[idx]);
        }

        return list;
    }

    private IEnumerable<int> GetRoundRobinIndices(int totalCount, int toCancel)
    {
        if (totalCount == 0) yield break;
        var start = (_lastRemovedIndex + 1) % totalCount;
        for (var i = 0; i < toCancel; i++)
        {
            yield return (start + i) % totalCount;
        }
        _lastRemovedIndex = (start + toCancel - 1) % totalCount;
    }

    private static IEnumerable<int> GetRandomIndices(int totalCount, int toCancel)
    {
        if (toCancel >= totalCount)
        {
            for (var i = 0; i < totalCount; i++) yield return i;
            yield break;
        }

        var selected = new HashSet<int>(toCancel);
        while (selected.Count < toCancel)
        {
            selected.Add(Random.Shared.Next(0, totalCount));
        }

        foreach (var idx in selected) yield return idx;
    }

    private async Task ReaderLoopAsync(CancellationTokenSource cts)
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

                using var ctsExec = CancellationTokenSource.CreateLinkedTokenSource(
                    cts.Token, item.CancellationToken);


                isContinue = await ExecuteItemAsync(item, ctsExec.Token).ConfigureAwait(false);
                if (!isContinue) break;

            }
        }
        catch (Exception ex)
        {
            LogReaderError(_logger, ex);
            await ShutdownAsync();
        }
        finally
        {
            RemoveReader(cts);
        }
    }

    private void RemoveReader(CancellationTokenSource cts)
    {
        lock (_readersSync)
        {
            _ctsReaders.Remove(cts);
            _readerCount--;
            _concurrency = _readerCount;
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
            LogItemAborted(_logger, _getItemDisplayName(item.Input), ex);
            return true;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            OnStatusChanged(item.Input, SaWorkStatus.Cancelled);
            LogItemCancelled(_logger, _getItemDisplayName(item.Input), ex);
            return false;
        }
        catch (Exception ex)
        {
            var dislpayItem = _getItemDisplayName(item.Input);

            SaExecutionErrorStrategy errorStrategy = SaExecutionErrorStrategy.ShutdownQueue;
            try
            {
                OnStatusChanged(item.Input, SaWorkStatus.Faulted, ex);
                LogItemExecutionFailed(_logger, dislpayItem, ex);

                errorStrategy = _handleItemFaulted(item.Input, ex);
            }
            catch (Exception callbackEx)
            {
                LogItemHandlerFailed(_logger, dislpayItem, callbackEx);
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
        _ = ShutdownAsync();
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
            if (--_taskCount == 0)
            {
                tcs = _idleTcs;
            }
        }
        tcs?.TrySetResult();
    }

    private void OnStatusChanged(TInput item, SaWorkStatus status, Exception? error = null)
    {
        if (_statusChanged is null) return;

        Exception? handlerEx = null;
        lock (_cbSync)
        {
            try
            {
                _statusChanged(item, status, error);
            }
            catch (Exception ex)
            {
                handlerEx = ex;
            }
        }

        if (handlerEx is not null)
        {
            LogItemHandlerFailed(_logger, _getItemDisplayName(item), handlerEx);
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
            await WaitForReadersToCompleteAsync();
            ClearRemainingItems();
        }
        catch (Exception ex)
        {
            LogShutdownError(_logger, ex);
        }
    }

    private async Task WaitForReadersToCompleteAsync()
    {
        Task[] tasks;
        lock (_readersSync)
        {
            tasks = [.. _taskReaders];
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public void Shutdown()
    {
        if (Interlocked.CompareExchange(ref _state, QueueState.Shutdown, QueueState.Active) != QueueState.Active)
            return;

        try
        {
            _shutdownCts.Cancel();
            _queue.Writer.TryComplete();

            Task[] tasks;
            lock (_readersSync)
            {
                tasks = [.. _taskReaders];
            }

            Task.WhenAll(tasks).GetAwaiter().GetResult();

            ClearRemainingItems();
        }
        catch (Exception ex)
        {
            LogShutdownError(_logger, ex);
        }
    }

    private void ClearRemainingItems()
    {
        OperationCanceledException? err = null;

        while (_queue.Reader.TryRead(out var item))
        {
            err ??= ThrowHelper.QueueShutdownException;
            OnStatusChanged(item.Input, SaWorkStatus.Faulted, err);
            MarkInactive();
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
    {
        Shutdown();
        CompleteDispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (IsEnabled)
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        CompleteDispose();
    }


    #region Logging Definitions (Source Generator)

    private static partial class LogMessages
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "[{Item}] processing was cancelled")]
        public static partial void ItemCancelled(ILogger logger, string item, Exception exception);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "[{Item}] processing was aborted")]
        public static partial void ItemAborted(ILogger logger, string item, Exception exception);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "[{Item}] execution failed")]
        public static partial void ItemExecutionFailed(ILogger logger, string item, Exception exception);

        [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "[{Item}] event handler failed for work item")]
        public static partial void ItemHandlerFailed(ILogger logger, string item, Exception exception);

        [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "ReaderTask error")]
        public static partial void ReaderError(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error during shutdown")]
        public static partial void ShutdownError(ILogger logger, Exception exception);
    }

    private static void LogItemCancelled(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) LogMessages.ItemCancelled(logger, item, ex);
    }

    private static void LogItemAborted(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) LogMessages.ItemAborted(logger, item, ex);
    }

    private static void LogItemExecutionFailed(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) LogMessages.ItemExecutionFailed(logger, item, ex);
    }

    private static void LogItemHandlerFailed(ILogger? logger, string item, Exception ex)
    {
        if (logger is not null) LogMessages.ItemHandlerFailed(logger, item, ex);
    }

    private static void LogReaderError(ILogger? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ReaderError(logger, ex);
    }

    private static void LogShutdownError(ILogger? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ShutdownError(logger, ex);
    }

    #endregion
}


internal static class ThrowHelper
{
    public static void QueueStopped() => throw new InvalidOperationException("Queue has been stopped.");
    public static OperationCanceledException QueueShutdownException { get; }
        = new OperationCanceledException("Queue shutdown");
}
