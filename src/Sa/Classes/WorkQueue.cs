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
    /// Gets the current number of items available from queue.
    /// </summary>
    int QueueCount { get; }

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
    private readonly HashSet<WorkItem> _queueItems = [];

    private readonly Lock _rootSync = new();
    private readonly Lock _activeReadersSync = new();

    private readonly ILogger? _logger = null;
    private readonly TimeProvider _timeProvider;
    public readonly IWork<TModel> _processor;
    public readonly IWorkObserver<TModel>? _watcher;
    private readonly Action<WorkInfo>? _statusChanged;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _processingTask;


    private volatile CancellationTokenSource? _throttleCts = new();
    private volatile TaskCompletionSource _idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _isEnabled = true;
    private volatile bool _disposed = false;


    private int _concurrency;
    private readonly int _maxConcurrency;
    private readonly int _queueCapacity;
    private int _queueCount;
    private long _taskIdCounter;

    private readonly List<Task> _activeReaders = [];


    public WorkQueue(WorkQueueOptions<TModel> options)
    {
        ArgumentNullException.ThrowIfNull(options.Processor);

        _processor = options.Processor;
        _watcher = options.Observer ?? _processor as IWorkObserver<TModel>;
        _timeProvider = options.TimeProvider ?? TimeProvider.System;
        _statusChanged = options.StatusChanged;
        _logger = options.Logger;

        _maxConcurrency = options.MaxConcurrencyLimit ?? Environment.ProcessorCount;

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

    public int QueueCount => _queueCount;

    public bool IsIdle()
    {
        lock (_rootSync)
        {
            return _queueItems.Count == 0 && _queue.Reader.Count == 0;
        }
    }

    public int ConcurrencyLimit
    {
        get => _concurrency;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);

            var newLimit = Math.Clamp(value, 0, _maxConcurrency);

            Interlocked.Exchange(ref _concurrency, newLimit);
        }
    }


    public int MaxConcurrency => _maxConcurrency;

    public int QueueCapacity => _queueCapacity;



    public async ValueTask Enqueue(TModel model, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled)
            throw new InvalidOperationException("Queue has been stopped.");

        var item = new WorkItem(model, cancellationToken)
        {
            Id = Interlocked.Increment(ref _taskIdCounter),
            Status = WorkStatus.Queued,
            EnqueuedTime = _timeProvider.GetUtcNow(),
        };

        await OnStatusChangedAsync(item).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);

        ActivateItem(item);
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

            Task[] readersCopy;
            lock (_activeReadersSync)
            {
                readersCopy = [.. _activeReaders];
            }
            await Task.WhenAll(readersCopy).ConfigureAwait(false);
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
            TaskCompletionSource tcs;
            lock (_rootSync)
            {
                if (IsIdle()) break; // Повторная проверка после входа в блокировку
                tcs = _idleTcs;
            }
            await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            // Запускаем начальных reader'ов
            await SpawnAdditionalReaders(_concurrency).ConfigureAwait(false);
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
            // Ждём появления элементов или изменения состояния
            if (await _queue.Reader.WaitToReadAsync(shutdownToken).ConfigureAwait(false))
            {
                // Убеждаемся что есть активные reader'ы
                await EnsureReadersActive().ConfigureAwait(false);
            }

            // Небольшая задержка чтобы не спамить
            await Task.Delay(100, shutdownToken).ConfigureAwait(false);
        }
    }

    private Task EnsureReadersActive()
    {
        lock (_activeReadersSync)
        {
            var activeCount = _activeReaders.Count(t => !t.IsCompleted);
            var targetCount = _concurrency;

            if (activeCount < targetCount && _isEnabled && !_disposed)
            {
                return SpawnAdditionalReaders(targetCount - activeCount);
            }
        }

        return Task.CompletedTask;
    }

    private Task SpawnAdditionalReaders(int count)
    {
        var tasks = new List<Task>(capacity: count);

        lock (_activeReadersSync)
        {
            for (int i = 0; i < count; i++)
            {
                Task readerTask = ReaderLoopAsync(_shutdownCts.Token);
                _activeReaders.Add(readerTask);
                tasks.Add(readerTask);
            }
        }

        // Не ждём запуска
        var _ = Task.WhenAll(tasks).ContinueWith(t =>
        {
            lock (_activeReadersSync)
            {
                foreach (var task in tasks)
                {
                    _activeReaders.Remove(task);
                }
            }
        });

        return Task.CompletedTask;
    }


    private async Task ReaderLoopAsync(CancellationToken shutdownToken)
    {

        while (!shutdownToken.IsCancellationRequested && _isEnabled)
        {

            // Создаём токен с учётом троттлинга
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                shutdownToken,
                _throttleCts?.Token ?? CancellationToken.None);

            try
            {
                var item = await _queue.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);

                // Запускаем выполнение без ожидания
                _ = ExecuteItemAsync(item, shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Троттлинг — перепроверяем условия
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Reader error");
                await Task.Delay(100, shutdownToken).ConfigureAwait(false);
            }
        }
    }


    private async Task ExecuteItemAsync(WorkItem item, CancellationToken shutdownToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownToken,
            item.CancellationToken);

        var ct = cts.Token;

        try
        {
            ct.ThrowIfCancellationRequested();

            item.StartedTime = _timeProvider.GetUtcNow();
            item.Status = WorkStatus.Running;
            await OnStatusChangedAsync(item).ConfigureAwait(false);

            await _processor.Execute(item.Model, ct).ConfigureAwait(false);

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
            _queueItems.Add(item);
            _queueCount = _queueItems.Count;

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
            _queueItems.Remove(item);
            _queueCount = _queueItems.Count;

            if (_queueItems.Count == 0 && _queue.Reader.Count == 0)
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
                await watcher.HandleChanges(item.Model, info, _shutdownCts.Token).ConfigureAwait(false);
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

        _isEnabled = false;
        _queue.Writer.TryComplete();

        _shutdownCts.Cancel();

        var throttle = Interlocked.Exchange(ref _throttleCts, null);
        throttle?.Cancel();

        _disposed = true;

        _shutdownCts.Dispose();
        throttle?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _isEnabled = false;
        _queue.Writer.TryComplete();

        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        var throttle = Interlocked.Exchange(ref _throttleCts, null);
        if (throttle != null)
        {
            await throttle.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _processingTask.ConfigureAwait(false);

            Task[] readersCopy;
            lock (_activeReadersSync)
            {
                readersCopy = [.. _activeReaders];
            }
            await Task.WhenAll(readersCopy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during synchronous disposal");
        }
        finally
        {
            _disposed = true;
            _shutdownCts.Dispose();
            throttle?.Dispose();
        }
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
    ILogger? Logger = null,
    BoundedChannelFullMode FullMode = BoundedChannelFullMode.Wait
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

    public WorkQueueOptions<TModel> WithMaxConcurrency(int limit)
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


    public WorkQueueOptions<TModel> WithFullMode(BoundedChannelFullMode mode) =>
        this with { FullMode = mode };

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
