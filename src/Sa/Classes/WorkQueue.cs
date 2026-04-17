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

/// <summary>
/// Интерфейс для наблюдения за изменениями состояния задач в очереди.
/// </summary>
internal interface IWorkObserver<in TInput>
{
    Task HandleChanges(TInput input, WorkInfo work, CancellationToken cancellationToken);
}

/// <summary>
/// Представляет состояние и метаданные задачи в очереди.
/// </summary>
internal sealed record WorkInfo(
    long Id,
    WorkStatus Status,
    DateTimeOffset EnqueuedTime,
    DateTimeOffset? StartedTime = null,
    DateTimeOffset? EndedTime = null,
    Exception? LastError = null
)
{
    public bool IsEmpty => Id == 0;
}

internal enum WorkStatus
{
    Queued,
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
    /// <summary>
    /// Показывает, включена ли обработка задач.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the current inputs from queue.
    /// </summary>
    int QueueTasks { get; }


    /// <summary>
    /// Определяет, свободна ли очередь (нет активных и нет в очереди).
    /// </summary>
    bool IsIdle();

    /// <summary>
    /// Максимальное количество параллельных задач (динамически изменяется).
    /// </summary>
    int ConcurrencyLimit { get; set; }


    public int MaxConcurrency { get; }


    public int QueueCapacity { get; }

    /// <summary>
    /// Добавляет задачу в очередь асинхронно.
    /// </summary>
    ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Блокируется до момента, когда очередь станет полностью пустой.
    /// </summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Завершает обработку и отменяет ожидание новых задач.
    /// </summary>
    Task ShutdownAsync();
}

internal sealed class WorkQueue<TInput> : IWorkQueue<TInput>
{
    private readonly Channel<WorkItem> _queue;
    private readonly HashSet<WorkItem> _workItems = [];

    private readonly Lock _rootSync = new();
    private readonly Lock _activeReadersSync = new();

    private readonly ILogger? _logger = null;
    private readonly TimeProvider _timeProvider;
    public readonly IWork<TInput> _processor;
    public readonly IWorkObserver<TInput>? _watcher;
    private readonly Action<WorkInfo>? _statusChanged;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _processingTask;


    private volatile TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _isEnabled = true;
    private volatile bool _disposed = false;


    private int _concurrency;
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;
    private int _workItemsCount;
    private long _taskIdCounter;

    private readonly List<ReaderTask> _activeReaders = [];


    public WorkQueue(WorkQueueOptions<TInput> options)
    {
        ArgumentNullException.ThrowIfNull(options.Processor);

        _processor = options.Processor;
        _watcher = options.Observer ?? _processor as IWorkObserver<TInput>;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _statusChanged = options.StatusChanged;
        _logger = options.Logger;

        _maxConcurrency = options.MaxConcurrency > 0
            ? options.MaxConcurrency.Value
            : Environment.ProcessorCount;

        _concurrency = Math.Clamp(
            options.ConcurrencyLimit ?? Environment.ProcessorCount,
            0,
            _maxConcurrency);

        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;

        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = options.FullMode
        });

        _processingTask = ProcessQueueAsync();
    }

    public bool IsEnabled => _isEnabled;

    public int QueueTasks => _workItemsCount;


    public bool IsIdle()
    {
        lock (_rootSync)
        {
            return _workItems.Count == 0 && _queue.Reader.Count == 0;
        }
    }

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var newLimit = Math.Clamp(value, 0, _maxConcurrency);
            Interlocked.Exchange(ref _concurrency, newLimit);
        }
    }


    public int MaxConcurrency => _maxConcurrency;

    public int QueueCapacity => _queueCapacity;



    public async ValueTask Enqueue(TInput input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled)
            throw new InvalidOperationException("Queue has been stopped.");

        var item = new WorkItem(input, cancellationToken)
        {
            Id = Interlocked.Increment(ref _taskIdCounter),
            Status = WorkStatus.Queued,
            EnqueuedTime = _timeProvider.GetUtcNow(),
        };

        await OnStatusChangedAsync(item).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);

        ActivateItem(item);
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token,
            cancellationToken);

        while (!IsIdle())
        {
            TaskCompletionSource tcs;
            lock (_rootSync)
            {
                if (IsIdle()) break;
                tcs = _idleTcs;
            }

            await Task.WhenAny(
                tcs.Task.WaitAsync(cts.Token),
                Task.Delay(3000, cts.Token)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// init readers
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        try
        {
            SpawnAdditionalReaders(_concurrency);
            await MonitorQueueAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Queue processing failed");
        }
    }

    private async Task MonitorQueueAsync()
    {
        var shutdownToken = _shutdownCts.Token;

        while (!shutdownToken.IsCancellationRequested)
        {
            if (await _queue.Reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                EnsureReadersActive();
            }

            await Task.Delay(100, shutdownToken).ConfigureAwait(false);
        }
    }

    private void EnsureReadersActive()
    {
        lock (_activeReadersSync)
        {
            if (!_isEnabled && _disposed) return;

            var activeCount = GetActiveCount();

            var targetCount = _concurrency;

            var delta = targetCount - activeCount;

            if (delta > 0)
            {
                SpawnAdditionalReaders(delta);
            }
            else if (delta < 0)
            {
                foreach (var item in _activeReaders.Take(Math.Abs(delta)))
                {
                    item.TokenSource.Cancel();
                }
            }
        }
    }

    private int GetActiveCount()
    {
        return _activeReaders
            .Count(t => !t.Task.IsCompleted || !t.TokenSource.IsCancellationRequested);
    }

    private void SpawnAdditionalReaders(int count)
    {
        void Delete(ReaderTask reader)
        {
            lock (_activeReadersSync)
            {
                _activeReaders.Remove(reader);
                reader.Dispose();
            }
        }

        lock (_activeReadersSync)
        {
            for (int i = 0; i < count; i++)
            {
                CancellationTokenSource tokenSource = new();
                Task readerTask = ReaderLoopAsync(tokenSource.Token);

                ReaderTask reader = new(readerTask, tokenSource);

                _activeReaders.Add(reader);

                readerTask.ContinueWith(t => Delete(reader));
            }
        }
    }


    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        var shutdownToken = _shutdownCts.Token;

        while (!cancellationToken.IsCancellationRequested
            && !shutdownToken.IsCancellationRequested
            && _isEnabled)
        {
            try
            {
                WorkItem item = await _queue.Reader.ReadAsync(shutdownToken).ConfigureAwait(false);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    shutdownToken,
                    cancellationToken,
                    item.CancellationToken);

                await ExecuteItemAsync(item, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ReaderTask error");
                await Task.Delay(100, shutdownToken).ConfigureAwait(false);
            }
        }
    }


    private async Task ExecuteItemAsync(WorkItem item, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            item.StartedTime = _timeProvider.GetUtcNow();
            item.Status = WorkStatus.Running;
            await OnStatusChangedAsync(item).ConfigureAwait(false);

            await _processor.Execute(item.Input, ct).ConfigureAwait(false);

            item.Status = WorkStatus.Completed;
        }
        catch (OperationCanceledException ex) when (item.CancellationToken.IsCancellationRequested)
        {
            item.Status = WorkStatus.Cancelled;

            if (_logger != null && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger?.LogWarning(ex, "Item {ItemId} processing was cancelled", item.Id);
            }
        }
        catch (Exception ex)
        {
            item.Status = WorkStatus.Faulted;
            item.LastError = ex;
            _logger?.LogError(ex, "Item {ItemId} execution failed", item.Id);
        }
        finally
        {
            if (!_disposed)
            {
                item.EndedTime = _timeProvider.GetUtcNow();
                await OnStatusChangedAsync(item).ConfigureAwait(false);
                DeactivateItem(item);
            }
        }
    }

    private void ActivateItem(WorkItem item)
    {
        TaskCompletionSource oldTcs;

        lock (_rootSync)
        {
            _workItems.Add(item);
            _workItemsCount = _workItems.Count;

            oldTcs = _idleTcs;
            _idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        oldTcs.TrySetResult();
    }

    private void DeactivateItem(WorkItem item)
    {
        TaskCompletionSource? tcsToNotify = null;

        lock (_rootSync)
        {
            _workItems.Remove(item);
            _workItemsCount = _workItems.Count;

            if (_workItems.Count == 0 && _queue.Reader.Count == 0)
            {
                tcsToNotify = _idleTcs;
            }
        }

        tcsToNotify?.TrySetResult();
    }

    private async Task OnStatusChangedAsync(WorkItem item)
    {
        var info = item.ToInfo();
        var watcher = _watcher;

        if (watcher != null)
        {
            try
            {
                await watcher.HandleChanges(item.Input, info, _shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Observer failed for work item #{WorkItemId}", info.Id);
            }
        }

        if (_statusChanged != null)
        {
            try
            {
                _statusChanged(info);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Event handler failed for work item #{WorkItemId}", info.Id);
            }

        }
    }

    public async Task ShutdownAsync()
    {
        lock (_rootSync)
        {
            if (!_isEnabled || _disposed) return;
            _isEnabled = false;
        }

        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _processingTask.ConfigureAwait(false);

            Task[] readers;
            lock (_activeReadersSync)
            {
                readers = [.. _activeReaders.Select(c => c.Task)];
            }
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown");
        }
        finally
        {
            await ClearAll().ConfigureAwait(false);
        }
    }

    private async Task ClearAll()
    {
        while (_queue.Reader.TryRead(out var input))
        {
            input.Status = WorkStatus.Faulted;
            input.LastError = ShutdownAsyncException.Default;
            input.EndedTime = _timeProvider.GetUtcNow();
            await OnStatusChangedAsync(input).ConfigureAwait(false);
            DeactivateItem(input);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _isEnabled = false;
        _queue.Writer.TryComplete();

        _shutdownCts.Cancel();

        _disposed = true;

        _shutdownCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            _isEnabled = false;
            _queue.Writer.TryComplete();
            await ShutdownAsync().ConfigureAwait(false);

        }
        finally
        {
            _disposed = true;
            _shutdownCts.Dispose();
        }
    }

    private sealed class WorkItem(TInput item, CancellationToken token)
    {
        public long Id { get; init; }
        public TInput Input => item;
        public CancellationToken CancellationToken => token;
        public WorkStatus Status { get; set; }
        public DateTimeOffset EnqueuedTime { get; init; }
        public DateTimeOffset? StartedTime { get; set; } = default;
        public DateTimeOffset? EndedTime { get; set; } = default;
        public Exception? LastError { get; set; } = null;

        public WorkInfo ToInfo() => new(Id, Status, EnqueuedTime, StartedTime, EndedTime, LastError);
    }

    internal sealed record ReaderTask(Task Task, CancellationTokenSource TokenSource) : IDisposable
    {
        public void Dispose()
        {
            TokenSource.Dispose();
        }
    }
}


internal sealed record WorkQueueOptions<TInput>
(
    // Required
    IWork<TInput> Processor,
    // Optional with defaults
    IWorkObserver<TInput>? Observer = null,

    TimeProvider? TimeProvider = null,
    int? QueueCapacity = null,
    int? ConcurrencyLimit = null,
    int? MaxConcurrency = null,
    bool? SingleWriter = false,
    Action<WorkInfo>? StatusChanged = null,
    ILogger? Logger = null,
    BoundedChannelFullMode FullMode = BoundedChannelFullMode.Wait
)
{
    public WorkQueueOptions<TInput> WithTimeProvider(TimeProvider timeProvider) =>
        this with { TimeProvider = timeProvider };

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
    {
        if (limit < 1)
        {
            limit = Environment.ProcessorCount;
        }
        return this with { MaxConcurrency = limit };
    }

    public WorkQueueOptions<TInput> WithSingleWriter(bool singleWriter) =>
        this with { SingleWriter = singleWriter };

    public WorkQueueOptions<TInput> WithStatusCallback(Action<WorkInfo> callback) =>
        this with { StatusChanged = callback };

    public WorkQueueOptions<TInput> WithLogger(ILogger<WorkQueue<TInput>> logger) =>
        this with { Logger = logger };


    public WorkQueueOptions<TInput> WithFullMode(BoundedChannelFullMode mode) =>
        this with { FullMode = mode };

    public WorkQueueOptions<TInput> WithObserver(Func<TInput, WorkInfo, CancellationToken, Task> process) =>
        this with { Observer = new WorkObserverProcessor(process) };

    public WorkQueueOptions<TInput> WithObserver(IWorkObserver<TInput> observer) =>
        this with { Observer = observer };


    // -- ct-or
    public static WorkQueueOptions<TInput> Create(IWork<TInput> processor) =>
        new(processor);

    public static WorkQueueOptions<TInput> Create(Func<TInput, CancellationToken, Task> process) =>
        new(new WorkProcessor(process));



    // -- wrap classes
    sealed class WorkProcessor(Func<TInput, CancellationToken, Task> process) : IWork<TInput>
    {
        public Task Execute(TInput input, CancellationToken cancellationToken)
            => process(input, cancellationToken);
    }

    sealed class WorkObserverProcessor(Func<TInput, WorkInfo, CancellationToken, Task> process) : IWorkObserver<TInput>
    {
        public Task HandleChanges(TInput input, WorkInfo work, CancellationToken cancellationToken)
            => process(input, work, cancellationToken);
    }
}



public sealed class ShutdownAsyncException : Exception
{
    public static ShutdownAsyncException Default => new();
}
