using System.Threading.Channels;

namespace Sa.Classes;


internal interface IWork<in TModel>
{
    Task Execute(TModel model, CancellationToken cancellationToken);
}

internal interface IWorkObserver<in TModel>
{
    Task HandleChanges(TModel model, WorkInfo work, CancellationToken cancellationToken);
}

internal record struct WorkInfo(
    long Id,
    WorkStatus Status,
    DateTimeOffset EnqueuedTime,
    DateTimeOffset? StartedTime = null,
    DateTimeOffset? EndedTime = null,
    Exception? LastError = null
)
{
    public readonly bool IsEmpty => Id == 0;
}

internal enum WorkStatus
{
    Queued,
    Running,
    Completed,
    Faulted,
    Cancelled
}

internal interface IWorkQueue<in TModel> : IDisposable, IAsyncDisposable
{
    bool IsEnabled { get; }
    int ActiveTasks { get; }
    bool IsIdle();
    int ConcurrencyLimit { get; set; }

    ValueTask Enqueue(TModel model, CancellationToken cancellationToken = default);

    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
    Task ShutdownAsync();

    event EventHandler<WorkInfo>? StatusChanged;
}

internal sealed class WorkQueue<TModel> : IWorkQueue<TModel>
{
    private readonly Channel<WorkItemSnapshot> _queue;

    private readonly List<WorkItemSnapshot> _activeItems = [];
    private readonly Lock _rootSync = new();

    private int _maxConcurrency = Environment.ProcessorCount;
    private long _taskIdCounter = 0;
    private bool _isEnabled = true;
    private bool _disposed = false;

    private readonly Task _processingTask;
    private readonly TimeProvider _timeProvider;

    public readonly IWork<TModel> _processor;
    public readonly IWorkObserver<TModel>? _watcher;

    private readonly CancellationTokenSource _shutdownCts = new();

    public event EventHandler<WorkInfo>? StatusChanged;

    public WorkQueue(
        IWork<TModel> processor,
        IWorkObserver<TModel>? observer = null,
        TimeProvider? timeProvider = null)
    {
        _processor = processor;
        _watcher = observer ?? processor as IWorkObserver<TModel>;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _queue = Channel.CreateUnbounded<WorkItemSnapshot>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = true,
            SingleReader = false,
            SingleWriter = false,
        });

        _processingTask = LoopAsync();
    }

    public bool IsEnabled => _isEnabled;
    public int ActiveTasks => _activeItems.Count;
    public int QueuedTasks => _queue.Reader.Count;

    public bool IsIdle()
    {
        lock (_rootSync)
        {
            return _activeItems.Count == 0 && _queue.Reader.Count == 0;
        }
    }

    public int ConcurrencyLimit
    {
        get => _maxConcurrency;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Must be at least 1.");
            _maxConcurrency = value;
        }
    }

    public async ValueTask Enqueue(TModel model, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled) throw new InvalidOperationException("Task scheduler has been stopped.");

        var shapshot = new WorkItemSnapshot(model, cancellationToken)
        {
            Id = Interlocked.Increment(ref _taskIdCounter),
            Status = WorkStatus.Queued,
            EnqueuedTime = _timeProvider.GetUtcNow(),
        };

        await OnStatusChanged(shapshot);

        await _queue.Writer.WriteAsync(shapshot, cancellationToken);
    }

    public async Task ShutdownAsync()
    {
        if (!_isEnabled || _disposed) return;

        _isEnabled = false;
        await _shutdownCts.CancelAsync();
        await _processingTask;

    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!IsIdle())
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task LoopAsync()
    {
        var ct = _shutdownCts.Token;
        try
        {
            while (!ct.IsCancellationRequested && await _queue.Reader.WaitToReadAsync(ct))
            {
                while (_isEnabled
                    && ActiveTasks < _maxConcurrency
                    && _queue.Reader.TryRead(out var snapshot))
                {
                    _ = Execute(snapshot); // Не ждём, запускаем параллельно
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
    }

    private async Task Execute(WorkItemSnapshot snapshot)
    {
        ActivateSnapshot(snapshot);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, snapshot.CancellationToken);
        var ct = cts.Token;
        try
        {
            ct.ThrowIfCancellationRequested();

            snapshot.StartedTime = _timeProvider.GetUtcNow();
            snapshot.Status = WorkStatus.Running;
            await OnStatusChanged(snapshot);

            await _processor.Execute(snapshot.Model, ct);
            snapshot.Status = WorkStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            snapshot.Status = WorkStatus.Cancelled;
        }
        catch (Exception ex)
        {
            snapshot.Status = WorkStatus.Faulted;
            snapshot.LastError = ex;
        }
        finally
        {
            snapshot.EndedTime = _timeProvider.GetUtcNow();
            await OnStatusChanged(snapshot);

            DeactiveSnapshot(snapshot);
        }
    }

    private void ActivateSnapshot(WorkItemSnapshot snapshot)
    {
        lock (_rootSync)
        {
            _activeItems.Add(snapshot);
        }
    }

    private void DeactiveSnapshot(WorkItemSnapshot snapshot)
    {
        lock (_rootSync)
        {
            _activeItems.Remove(snapshot);
        }
    }

    private async Task OnStatusChanged(WorkItemSnapshot snapshot)
    {
        var info = snapshot.ToInfo();

        if (_watcher != null)
        {
            try
            {
                await _watcher.HandleChanges(snapshot.Model, info, _shutdownCts.Token);
            }
            catch { /* ignore */ }
        }

        try
        {
            StatusChanged?.Invoke(this, info);
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _isEnabled = false;
        _queue.Writer.Complete();

        _shutdownCts.Cancel();
        _processingTask.Wait(5000);

        _shutdownCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _isEnabled = false;
        _queue.Writer.Complete();

        await _shutdownCts.CancelAsync();
        await _processingTask;

        _shutdownCts.Dispose();
    }

    private sealed class WorkItemSnapshot(TModel item, CancellationToken token)
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
