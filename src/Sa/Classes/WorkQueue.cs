using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Sa.Classes;


public interface IWork<in TModel>
{
    Task Execute(TModel model, CancellationToken cancellationToken);
}


public interface IWorkObserver<in TModel> : IWork<TModel>
{
    Task HandleChanges(TModel model, WorkInfo work, CancellationToken cancellationToken);
}



public enum WorkPriority
{
    High = 0,
    Normal = 1,
    Low = 2
}

public record WorkInfo(
    long Id,
    WorkPriority Priority,
    WorkStatus Status,
    DateTimeOffset EnqueuedTime,
    DateTimeOffset? StartedTime = null,
    DateTimeOffset? EndedTime = null,
    Exception? LastError = null
);

public enum WorkStatus
{
    Queued,
    Running,
    Completed,
    Faulted,
    Cancelled
}

public interface IWorkQueue<in TModel>
{
    bool IsEnabled { get; }
    int ActiveTasks { get; }
    int QueuedTasks { get; }
    int ConcurrencyLimit { get; set; }

    ValueTask Enqueue(
        TModel model,
        WorkPriority priority = WorkPriority.Normal,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync();

    event EventHandler<WorkInfo>? StatusChanged;
}



/// <summary>
/// A prioritized task scheduler with backpressure, dynamic queue sizing, and observability via <see cref="GetTaskStream"/>.
/// Metrics can be derived externally from the task stream.
/// </summary>
public sealed class WorkQueue<TModel> : IWorkQueue<TModel>, IDisposable, IAsyncDisposable
{
    private readonly Channel<WorkItemSnapshot> _queue;
    private readonly List<WorkItemSnapshot> _activeItems = [];
    private readonly Lock _lock = new();

    private int _maxConcurrency = Environment.ProcessorCount;
    private long _taskIdCounter = 0;
    private int _activeTasksCounter = 0;
    private bool _isEnabled = true;
    private bool _disposed = false;

    private readonly Lazy<Task> _processingTask;
    private readonly TimeProvider _timeProvider;

    public readonly IWork<TModel> _processor;
    public readonly IWorkObserver<TModel>? _watcher;

    private readonly CancellationTokenSource _shutdownCts = new();

    public event EventHandler<WorkInfo>? StatusChanged;

    public WorkQueue(
        IWork<TModel> processor,
        TimeProvider? timeProvider = null)
    {
        _processor = processor;
        _watcher = processor as IWorkObserver<TModel>;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _queue = Channel.CreateUnboundedPrioritized(new UnboundedPrioritizedChannelOptions<WorkItemSnapshot>
        {
            Comparer = new WorkSnapshotPriorityComparer(),
            AllowSynchronousContinuations = true,
            SingleReader = false,
            SingleWriter = false,
        });

        _processingTask = new Lazy<Task>(Loop);
    }

    public bool IsEnabled => _isEnabled;
    public int ActiveTasks => _activeTasksCounter;
    public int QueuedTasks => _queue.Reader.Count;

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

    public async ValueTask Enqueue(
        TModel model,
        WorkPriority priority = WorkPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled) throw new InvalidOperationException("Task scheduler has been stopped.");

        var info = new WorkItemSnapshot(model)
        {
            Id = Interlocked.Increment(ref _taskIdCounter),
            Priority = priority,
            Status = WorkStatus.Queued,
            EnqueuedTime = _timeProvider.GetUtcNow(),
        };

        var writer = _queue.Writer;

        await writer.WriteAsync(info, cancellationToken).ConfigureAwait(false);

        await OnTaskUpdated(info).ConfigureAwait(false);

        _ = _processingTask.Value;
    }

    public async Task ShutdownAsync()
    {
        if (!_isEnabled || _disposed) return;

        _isEnabled = false;
        await _shutdownCts.CancelAsync();

        try
        {
            await _processingTask.Value.ConfigureAwait(false);
        }
        catch { /* ignore */ }
    }

    public async Task WaitForCompletionAsync(CancellationToken ct = default)
    {
        while (_activeTasksCounter > 0 || !IsQueueEmpty())
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private async Task Loop()
    {
        var ct = _shutdownCts.Token;

        while (!ct.IsCancellationRequested && await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_isEnabled && _activeTasksCounter < _maxConcurrency && TryDequeue(out var task))
            {
                _ = Execute(task); // Не ждём, запускаем параллельно
            }
        }
    }

    private async Task Execute(WorkItemSnapshot task)
    {
        Interlocked.Increment(ref _activeTasksCounter);

        lock (_lock)
        {
            _activeItems.Add(task);
        }

        task.StartedTime = _timeProvider.GetUtcNow();
        task.Status = WorkStatus.Running;
        await OnTaskUpdated(task).ConfigureAwait(false);

        var ct = _shutdownCts.Token;
        try
        {
            await _processor.Execute(task.Model, ct).ConfigureAwait(false);
            task.Status = WorkStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            task.Status = WorkStatus.Cancelled;
        }
        catch (Exception ex)
        {
            task.Status = WorkStatus.Faulted;
            task.LastError = ex;
        }
        finally
        {
            task.EndedTime = _timeProvider.GetUtcNow();
            lock (_lock)
            {
                _activeItems.Remove(task);
            }

            Interlocked.Decrement(ref _activeTasksCounter);
            await OnTaskUpdated(task).ConfigureAwait(false);
        }
    }

    private bool TryDequeue([MaybeNullWhen(false)] out WorkItemSnapshot info)
    {
        return _queue.Reader.TryRead(out info);
    }

    private bool IsQueueEmpty() => !_queue.Reader.CanPeek;

    private async Task OnTaskUpdated(WorkItemSnapshot task)
    {
        var info = task.ToInfo();

        if (_watcher != null)
        {
            try
            {
                await _watcher.HandleChanges(task.Model, info, _shutdownCts.Token).ConfigureAwait(false);
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

        try
        {
            Task.Run(async () => await DisposeAsync().AsTask())
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* ignored */ }
        finally
        {
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _isEnabled = false;
        _queue.Writer.Complete();

        await _shutdownCts.CancelAsync();

        if (_processingTask.IsValueCreated)
        {
            try
            {
                await _processingTask.Value;
            }
            catch { /* ignore */ }
        }

        _shutdownCts.Dispose();
    }


    private sealed class WorkSnapshotPriorityComparer : IComparer<WorkItemSnapshot>
    {
        int IComparer<WorkItemSnapshot>.Compare(WorkItemSnapshot? x, WorkItemSnapshot? y)
        {
            if (x is null) return y is null ? 0 : 1;
            if (y is null) return -1;

            var priorityComparison = x.Priority.CompareTo(y.Priority);

            if (priorityComparison != 0)
                return priorityComparison;

            return x.EnqueuedTime.CompareTo(y.EnqueuedTime);
        }
    }

    private sealed class WorkItemSnapshot(TModel item)
    {
        public long Id { get; init; }

        public TModel Model => item;

        public WorkPriority Priority { get; init; }
        public WorkStatus Status { get; set; }
        public DateTimeOffset EnqueuedTime { get; init; }
        public DateTimeOffset? StartedTime { get; set; } = default;
        public DateTimeOffset? EndedTime { get; set; } = default;
        public Exception? LastError { get; set; } = null;

        public WorkInfo ToInfo() => new(Id, Priority, Status, EnqueuedTime, StartedTime, EndedTime, LastError);
    }
}
