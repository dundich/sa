using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Sa.Classes;


/// <summary>
/// Интерфейс для выполнения бизнес-логики.
/// </summary>
internal interface IWork<in TModel>
{
    Task Execute(TModel model, CancellationToken cancellationToken);
}

/// <summary>
/// Интерфейс для наблюдения за изменениями состояния задач в очереди.
/// </summary>
internal interface IWorkObserver<in TModel>
{
    Task HandleChanges(TModel model, WorkInfo work, CancellationToken cancellationToken);
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
internal interface IWorkQueue<in TModel> : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Показывает, включена ли обработка задач.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Текущее количество активно выполняющихся задач.
    /// </summary>
    int ActiveTasks { get; }

    /// <summary>
    ///  Gets the current number of items available from this channel reader.
    /// </summary>
    int QueuedTasks { get; }

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
    ValueTask Enqueue(TModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Блокируется до момента, когда очередь станет полностью пустой.
    /// </summary>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Завершает обработку и отменяет ожидание новых задач.
    /// </summary>
    Task ShutdownAsync();
}

internal sealed class WorkQueue<TModel> : IWorkQueue<TModel>
{
    private readonly Channel<WorkItem> _queue;
    private readonly HashSet<long> _activeItemIds = [];
    private readonly Lock _rootSync = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly ILogger? _logger = null;
    private readonly TimeProvider _timeProvider;
    public readonly IWork<TModel> _processor;
    public readonly IWorkObserver<TModel>? _watcher;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _processingTask;
    private readonly Action<WorkInfo>? _statusChanged;

    private volatile TaskCompletionSource _idleTcs = new();
    private int _activeCount = 0;
    private volatile bool _isEnabled = true;

    private int _сoncurrency;
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;

    private long _taskIdCounter = 0;
    private bool _disposed = false;

    public WorkQueue(WorkQueueOptions<TModel> options)
    {
        _processor = options.Processor;
        _watcher = options.Observer ?? _processor as IWorkObserver<TModel>;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _сoncurrency = options.ConcurrencyLimit ?? Environment.ProcessorCount;
        _statusChanged = options.StatusChanged;
        _logger = options.Logger;

        _maxConcurrency = Math.Max(options.MaxConcurrencyLimit ?? Environment.ProcessorCount, _сoncurrency);
        _queueCapacity = options.QueueCapacity ?? _maxConcurrency;

        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_queueCapacity)
        {
            AllowSynchronousContinuations = true,
            SingleReader = false,
            SingleWriter = options.SingleWriter ?? false,
            FullMode = BoundedChannelFullMode.Wait
        });


        _concurrencySemaphore = new SemaphoreSlim(_сoncurrency, _maxConcurrency);

        _processingTask = LoopAsync();
    }

    public bool IsEnabled => _isEnabled;
    public int ActiveTasks => _activeCount;
    public int QueuedTasks => _queue.Reader.Count;

    public bool IsIdle()
    {
        lock (_rootSync)
        {
            return _activeItemIds.Count == 0 && _queue.Reader.Count == 0;
        }
    }

    public int ConcurrencyLimit
    {
        get => _сoncurrency;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);

            var oldValue = Interlocked.Exchange(ref _сoncurrency, value);
            int delta = value - oldValue;

            if (delta > 0)
            {
                try
                {
                    _concurrencySemaphore.Release(delta);
                }
                catch (SemaphoreFullException)
                {
                    // Игнорируем, если семафор уже на максимуме
                }
            }
        }
    }

    public int MaxConcurrency => _maxConcurrency;

    public int QueueCapacity => _queueCapacity;

    public async ValueTask Enqueue(TModel model, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled)
            throw new InvalidOperationException("Task scheduler has been stopped.");

        var snapshot = new WorkItem(model, cancellationToken)
        {
            Id = Interlocked.Increment(ref _taskIdCounter),
            Status = WorkStatus.Queued,
            EnqueuedTime = _timeProvider.GetUtcNow(),
        };

        await OnStatusChangedAsync(snapshot);
        await _queue.Writer.WriteAsync(snapshot, cancellationToken);
    }

    public async Task ShutdownAsync()
    {
        lock (_rootSync)
        {
            if (!_isEnabled || _disposed) return;
            _isEnabled = false;
        }

        await _shutdownCts.CancelAsync();

        try
        {
            await _processingTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown");
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (!IsIdle())
        {
            var currentTcs = _idleTcs;
            await currentTcs.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task LoopAsync()
    {
        var ct = _shutdownCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && await _queue.Reader.WaitToReadAsync(ct))
            {
                // Каждый поток пытается читать, пока есть данные
                while (_isEnabled && _queue.Reader.TryRead(out var item))
                {
                    // SemaphoreSlim автоматически ограничит реальную параллельность
                    _ = ExecuteWithConcurrencyControlAsync(item, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error in queue processing loop");
        }
    }

    // Обёртка: захват слота -> выполнение -> освобождение
    private async Task ExecuteWithConcurrencyControlAsync(WorkItem item, CancellationToken loopCt)
    {
        // Ждём доступного слота (с учётом отмены)
        try
        {
            await _concurrencySemaphore.WaitAsync(loopCt);
        }
        catch (OperationCanceledException)
        {
            // Если отменили ожидание слота — возвращаем задачу в очередь или отменяем
            await CancelWorkItemAsync(item);
            return;
        }


        try
        {
            // Проверяем, не остановили ли очередь пока ждали слот
            if (!_isEnabled)
            {
                await CancelWorkItemAsync(item);
                return;
            }

            await ExecuteAsync(item);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled error executing work item #{WorkItemId}", item.Id);

            if (item.Status is WorkStatus.Queued or WorkStatus.Running)
            {
                item.Status = WorkStatus.Faulted;
                item.LastError = ex;
                item.EndedTime = _timeProvider.GetUtcNow();
                await OnStatusChangedAsync(item);
            }
        }
        finally
        {
            _concurrencySemaphore.Release(); // Освобождаем слот
        }
    }

    private async Task CancelWorkItemAsync(WorkItem item)
    {
        item.Status = WorkStatus.Cancelled;
        item.EndedTime = _timeProvider.GetUtcNow();
        await OnStatusChangedAsync(item);
    }

    private async Task ExecuteAsync(WorkItem item)
    {
        ActivateItem(item);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token,
            item.CancellationToken);

        var ct = cts.Token;

        try
        {
            ct.ThrowIfCancellationRequested();

            item.StartedTime = _timeProvider.GetUtcNow();
            item.Status = WorkStatus.Running;
            await OnStatusChangedAsync(item);

            await _processor.Execute(item.Model, ct);
            item.Status = WorkStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Status = WorkStatus.Cancelled;
        }
        catch (Exception ex)
        {
            item.Status = WorkStatus.Faulted;
            item.LastError = ex;
        }
        finally
        {
            item.EndedTime = _timeProvider.GetUtcNow();
            await OnStatusChangedAsync(item);
            DeactivateItem(item);
        }
    }

    private void ActivateItem(WorkItem item)
    {
        TaskCompletionSource oldTcs;

        lock (_rootSync)
        {
            _activeItemIds.Add(item.Id);
            _activeCount = _activeItemIds.Count;

            oldTcs = _idleTcs;
            _idleTcs = new TaskCompletionSource();
        }

        oldTcs.TrySetResult();
    }

    private void DeactivateItem(WorkItem item)
    {
        TaskCompletionSource? tcsToNotify = null;

        lock (_rootSync)
        {
            _activeItemIds.Remove(item.Id);
            _activeCount = _activeItemIds.Count;

            if (_activeItemIds.Count == 0 && _queue.Reader.Count == 0)
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
                await watcher.HandleChanges(item.Model, info, _shutdownCts.Token);
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

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _isEnabled = false;
        _queue.Writer.TryComplete();
        _shutdownCts.Cancel();

        _concurrencySemaphore.Dispose();
        _shutdownCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _isEnabled = false;
        _queue.Writer.TryComplete();

        await _shutdownCts.CancelAsync();
        try
        {
            await _processingTask;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during synchronous disposal");
        }
        _concurrencySemaphore.Dispose();
        _shutdownCts.Dispose();
    }

    private sealed class WorkItem(TModel item, CancellationToken token)
    {
        public long Id { get; init; }
        public TModel Model => item;
        public CancellationToken CancellationToken => token;
        public WorkStatus Status { get; set; }
        public DateTimeOffset EnqueuedTime { get; init; }
        public DateTimeOffset? StartedTime { get; set; } = default;
        public DateTimeOffset? EndedTime { get; set; } = default;
        public Exception? LastError { get; set; } = null;

        public WorkInfo ToInfo() => new(Id, Status, EnqueuedTime, StartedTime, EndedTime, LastError);
    }
}


internal sealed record WorkQueueOptions<TModel>
(
    // Required
    IWork<TModel> Processor,
    // Optional with defaults
    IWorkObserver<TModel>? Observer = null,
    TimeProvider? TimeProvider = null,
    int? QueueCapacity = null,
    int? ConcurrencyLimit = null,
    int? MaxConcurrencyLimit = null,
    bool? SingleWriter = false,
    Action<WorkInfo>? StatusChanged = null,
    ILogger? Logger = null
)
{
    public WorkQueueOptions<TModel> WithObserver(IWorkObserver<TModel> observer) =>
        this with { Observer = observer };

    public WorkQueueOptions<TModel> WithTimeProvider(TimeProvider timeProvider) =>
        this with { TimeProvider = timeProvider };

    public WorkQueueOptions<TModel> WithQueueCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        return this with { QueueCapacity = capacity };
    }

    public WorkQueueOptions<TModel> WithConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        return this with { ConcurrencyLimit = limit };
    }

    public WorkQueueOptions<TModel> WithMaxConcurrencyLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 0);
        return this with { MaxConcurrencyLimit = limit };
    }

    public WorkQueueOptions<TModel> WithSingleWriter(bool singleWriter) =>
        this with { SingleWriter = singleWriter };

    public WorkQueueOptions<TModel> WithStatusCallback(Action<WorkInfo> callback) =>
        this with { StatusChanged = callback };

    public WorkQueueOptions<TModel> WithLogger(ILogger<WorkQueue<TModel>> logger) =>
        this with { Logger = logger };

    public static WorkQueueOptions<TModel> Create(IWork<TModel> processor) =>
        new(processor);

    public static WorkQueueOptions<TModel> Create(Func<TModel, CancellationToken, Task> process) =>
        new(new WrapProcessor(process));

    sealed class WrapProcessor(Func<TModel, CancellationToken, Task> process) : IWork<TModel>
    {
        public Task Execute(TModel model, CancellationToken cancellationToken)
            => process(model, cancellationToken);
    }
}
