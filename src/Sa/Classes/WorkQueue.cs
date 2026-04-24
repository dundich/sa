using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Sa.Classes;

/// <summary>
/// Интерфейс для выполнения бизнес-логики.
/// </summary>
internal interface IWork<in TInput>
{
    Task Execute(TInput input, CancellationToken cancellationToken);
}

internal enum WorkStatus
{
    Running,
    Completed,
    Faulted,
    Cancelled
}

/// <summary>
/// Асинхронная очередь задач с ограничением параллелизма.
/// </summary>
internal interface IWorkQueue<in TInput> : IDisposable, IAsyncDisposable
{
    bool IsEnabled { get; }
    int QueueTasks { get; }
    bool IsIdle();
    int ConcurrencyLimit { get; set; }
    int MaxConcurrency { get; }
    int QueueCapacity { get; }

    ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default);
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync();
}


internal record WorkItem<TInput>(TInput Input, CancellationToken CancellationToken);


internal sealed partial class WorkQueue<TInput> : IWorkQueue<TInput>
{
    private enum QueueState
    {
        Active = 0,
        ShuttingDown = 1,
        Shutdown = 2,
        Disposed = 3
    }

    private readonly Channel<WorkItem<TInput>> _queue;

    private readonly Lock _rootSync = new();
    private readonly Lock _readersSync = new();

    private readonly ILogger<WorkQueue<TInput>>? _logger;

    private readonly IWork<TInput> _processor;

    private readonly Action<TInput, WorkStatus, Exception?>? _statusChanged;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;

    private volatile int _concurrency;
    private volatile bool _isEnabled = true;
    /// <summary>
    /// 0: Active, 1: ShuttingDown, 2: Shutdown, 3: Disposed
    /// </summary>
    private volatile QueueState _state;

    // Lock-free счётчик активных + ожидающих задач
    private volatile int _pendingAndActiveCount;
    private TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);


    private readonly List<ReaderContext> _activeReaders = [];

    public WorkQueue(WorkQueueOptions<TInput> options, ILogger<WorkQueue<TInput>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options.Processor);

        _logger = logger;

        _processor = options.Processor;

        _statusChanged = options.StatusChanged;

        _maxConcurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency.Value : Environment.ProcessorCount;
        _concurrency = Math.Clamp(options.ConcurrencyLimit ?? Environment.ProcessorCount, 0, _maxConcurrency);
        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;


        _queue = Channel.CreateBounded<WorkItem<TInput>>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = options.FullMode
        });

        // Запускаем читателей сразу
        SpawnReaders(_concurrency);
    }

    public bool IsEnabled => _isEnabled;

    public int QueueTasks => _pendingAndActiveCount;

    public bool IsIdle()
    {
        lock (_rootSync)
        {
            return _pendingAndActiveCount <= 0 && _queue.Reader.Count == 0;
        }
    }

    public int MaxConcurrency => _maxConcurrency;

    public int QueueCapacity => _queueCapacity;

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
            if (!_isEnabled) ThrowHelper.QueueStopped();

            var newLimit = Math.Clamp(value, 0, _maxConcurrency);
            var oldLimit = Interlocked.Exchange(ref _concurrency, newLimit);
            if (newLimit != oldLimit) AdjustReaders(newLimit, oldLimit);
        }
    }

    public async ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_state == QueueState.Disposed, this);
        if (!_isEnabled) ThrowHelper.QueueStopped();

        MarkActive();

        try
        {
            await _queue.Writer.WriteAsync(new(input, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            MarkInactive(); // Откат счётчика при ошибке записи
            throw;
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
            lock (_rootSync) { tcs = _idleTcs; }

            await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
    }

    #region Reader Management

    private void SpawnReaders(int count)
    {
        lock (_readersSync)
        {
            for (var i = 0; i < count; i++)
            {
                var cts = new CancellationTokenSource();
                var task = ReaderLoopAsync(cts.Token).ContinueWith(c => c.Dispose());
                var ctx = new ReaderContext(task, cts);
                _activeReaders.Add(ctx);
            }
        }
    }


    private void AdjustReaders(int target, int current)
    {
        lock (_readersSync)
        {
            if (_state > 0) return; // Не масштабируем при завершении

            var delta = target - current;
            if (delta > 0)
            {
                SpawnReaders(delta);
            }
            else if (delta < 0)
            {
                // Отменяем лишних читателей с конца списка
                var toCancel = Math.Min(-delta, _activeReaders.Count);
                for (var i = 0; i < toCancel; i++)
                {
                    _activeReaders[^1].StopTokenSource.Cancel();
                    _activeReaders.RemoveAt(_activeReaders.Count - 1);
                }
            }
        }
    }

    private async Task ReaderLoopAsync(CancellationToken stopToken)
    {
        var shutdownToken = _shutdownCts.Token;
        try
        {
            while (!stopToken.IsCancellationRequested
                && !shutdownToken.IsCancellationRequested
                && _isEnabled)
            {
                try
                {
                    var item = await _queue.Reader.ReadAsync(shutdownToken).ConfigureAwait(false);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                        shutdownToken,
                        stopToken,
                        item.CancellationToken);

                    await ExecuteItemAsync(item, cts.Token).ConfigureAwait(false);
                }
                catch (ChannelClosedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogReaderError(_logger, ex);
                    await Task.Delay(100, shutdownToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            UnregisterReader();
        }
    }

    private void UnregisterReader()
    {
        lock (_readersSync)
        {
            // Удаляем завершившегося читателя (если он ещё не был удалён при масштабировании)
            _activeReaders.RemoveAll(c => c.Task.IsCompleted);
        }
    }

    #endregion

    #region Execution & State

    private async Task ExecuteItemAsync(WorkItem<TInput> item, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            OnStatusChanged(item.Input, WorkStatus.Running);

            await _processor.Execute(item.Input, ct).ConfigureAwait(false);
            OnStatusChanged(item.Input, WorkStatus.Completed);
        }
        catch (OperationCanceledException ex) when (item.CancellationToken.IsCancellationRequested)
        {
            OnStatusChanged(item.Input, WorkStatus.Cancelled);
            LogItemCancelled(_logger, ex);
        }
        catch (Exception ex)
        {
            OnStatusChanged(item.Input, WorkStatus.Faulted, ex);
            LogItemExecutionFailed(_logger, ex);
        }
        finally
        {
            if (_state < QueueState.Shutdown)
            {
                MarkInactive();
            }
        }
    }

    private void MarkActive()
    {
        if (Interlocked.Increment(ref _pendingAndActiveCount) == 1)
        {
            TaskCompletionSource tcs;

            lock (_rootSync)
            {
                tcs = _idleTcs;
                // Переход из Idle в Active: создаём новый TCS
                _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            tcs.TrySetResult();
        }
    }

    private void MarkInactive()
    {
        if (Interlocked.Decrement(ref _pendingAndActiveCount) == 0)
        {
            TaskCompletionSource tcs;
            lock (_rootSync) { tcs = _idleTcs; }
            tcs.TrySetResult();
        }
    }

    private void OnStatusChanged(TInput item, WorkStatus status, Exception? error = null)
    {
        if (_statusChanged is not null)
        {
            try { _statusChanged(item, status, error); }
            catch (Exception ex) { LogEventHandlerFailed(_logger, ex); }
        }
    }

    #endregion

    #region Shutdown & Dispose

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _state, QueueState.ShuttingDown) != QueueState.Active) return; // Idempotent

        _isEnabled = false;

        try
        {
            await _shutdownCts.CancelAsync().ConfigureAwait(false);
            _queue.Writer.TryComplete();

            Task[] readers;
            lock (_readersSync) { readers = [.. _activeReaders.Select(c => c.Task)]; }
            await Task.WhenAll(readers).ConfigureAwait(false);

            await ClearRemainingItems().ConfigureAwait(false);

        }
        catch (Exception ex)
        {
            // Логируем ошибку, если что-то пошло не так при остановке
            LogShutdownError(_logger, ex);
        }
        finally
        {
            _state = QueueState.ShuttingDown;

            lock (_rootSync)
            {
                _idleTcs.TrySetResult();
            }
        }
    }

    private async Task ClearRemainingItems()
    {
        OperationCanceledException? err = null;

        while (_queue.Reader.TryRead(out var item))
        {
            err ??= new OperationCanceledException("Queue shutdown");

            OnStatusChanged(item.Input, WorkStatus.Faulted, err);
            MarkInactive();
        }
    }

    private void CompleteDispose()
    {
        Interlocked.Exchange(ref _state, QueueState.Disposed);
        _shutdownCts.Dispose();
        lock (_rootSync) _idleTcs.TrySetResult();
    }

    public void Dispose()
    {
        if (IsEnabled)
        {
            _isEnabled = false;
            _shutdownCts.Cancel();
            _queue.Writer.TryComplete();
        }

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

    #endregion


    private sealed record ReaderContext(Task Task, CancellationTokenSource StopTokenSource);


    #region Logging Definitions (Source Generator)

    private static partial class LogMessages
    {
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "Item processing was cancelled")]
        public static partial void ItemCancelled(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Error,
            Message = "Item execution failed")]
        public static partial void ItemExecutionFailed(ILogger logger, Exception exception);


        [LoggerMessage(
            EventId = 3,
            Level = LogLevel.Error,
            Message = "Event handler failed for work item")]
        public static partial void EventHandlerFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 4,
            Level = LogLevel.Error,
            Message = "ReaderTask error")]
        public static partial void ReaderError(ILogger logger, Exception exception);


        [LoggerMessage(
            EventId = 5,
            Level = LogLevel.Error,
            Message = "Error during shutdown")]
        public static partial void ShutdownError(ILogger logger, Exception exception);
    }

    // Статические методы-обертки для удобного вызова из экземпляра класса
    private static void LogItemCancelled(ILogger<WorkQueue<TInput>>? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ItemCancelled(logger, ex);
    }

    private static void LogItemExecutionFailed(ILogger<WorkQueue<TInput>>? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ItemExecutionFailed(logger, ex);
    }

    private static void LogEventHandlerFailed(ILogger<WorkQueue<TInput>>? logger, Exception ex)
    {
        if (logger is not null) LogMessages.EventHandlerFailed(logger, ex);
    }

    private static void LogReaderError(ILogger<WorkQueue<TInput>>? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ReaderError(logger, ex);
    }

    private static void LogShutdownError(ILogger<WorkQueue<TInput>>? logger, Exception ex)
    {
        if (logger is not null) LogMessages.ShutdownError(logger, ex);
    }

    #endregion
}

internal sealed record WorkQueueOptions<TInput>(
    IWork<TInput> Processor,
    int? QueueCapacity = null,
    int? ConcurrencyLimit = null,
    int? MaxConcurrency = null,
    bool? SingleWriter = false,
    Action<TInput, WorkStatus, Exception?>? StatusChanged = null,
    BoundedChannelFullMode FullMode = BoundedChannelFullMode.Wait)
{
    public WorkQueueOptions<TInput> WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        return this with { QueueCapacity = capacity };
    }

    public WorkQueueOptions<TInput> WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        return this with { ConcurrencyLimit = limit };
    }

    public WorkQueueOptions<TInput> WithMaxConcurrency(int limit)
        => this with { MaxConcurrency = limit < 1 ? Environment.ProcessorCount : limit };

    public WorkQueueOptions<TInput> WithSingleWriter(bool sw)
        => this with { SingleWriter = sw };

    public WorkQueueOptions<TInput> WithStatusCallback(Action<TInput, WorkStatus, Exception?> cb)
        => this with { StatusChanged = cb };

    public WorkQueueOptions<TInput> WithFullMode(BoundedChannelFullMode mode)
        => this with { FullMode = mode };


    public static WorkQueueOptions<TInput> Create(IWork<TInput> processor) => new(processor);

    public static WorkQueueOptions<TInput> Create(Func<TInput, CancellationToken, Task> process)
        => new(new DelegatingWork(process));

    private sealed class DelegatingWork(Func<TInput, CancellationToken, Task> process) : IWork<TInput>
    {
        public Task Execute(TInput input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }

}

internal static class ThrowHelper
{
    public static void QueueStopped() => throw new InvalidOperationException("Queue has been stopped.");
}
