using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Sa.Classes;

public enum TaskPriorityLevel
{
    High = 0,
    Normal = 1,
    Low = 2
}


public interface IPriorityTaskScheduler
{
    /// <summary>
    /// Gets whether the scheduler is currently accepting new tasks.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets the number of currently executing tasks.
    /// </summary>
    int ActiveTasks { get; }

    /// <summary>
    /// Gets the total number of queued (pending) tasks.
    /// </summary>
    int QueuedTasks { get; }

    /// <summary>
    /// Gets or sets the maximum number of concurrently executing tasks.
    /// Setting a higher value may trigger immediate processing of queued tasks.
    /// </summary>
    int ConcurrencyLimit { get; set; }

    /// <summary>
    /// Raised when all tasks have completed and no more are queued.
    /// </summary>
    event EventHandler? AllTasksCompleted;

    /// <summary>
    /// Raised when a task is cancelled.
    /// </summary>
    event EventHandler<int>? TaskCancelled;

    /// <summary>
    /// Raised when a task completes with an unobserved exception.
    /// </summary>
    event EventHandler<(Exception ex, int taskId)>? UnobservedException;

    /// <summary>
    /// Enqueues a task with optional priority and cancellation token.
    /// Returns a unique ID that can be used to cancel the task later.
    /// </summary>
    ValueTask<int> Enqueue(
        Func<CancellationToken, Task> taskFunc,
        TaskPriorityLevel priority = TaskPriorityLevel.Normal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to cancel a specific task by ID.
    /// </summary>
    ValueTask<bool> CancelTaskAsync(int taskId);

    /// <summary>
    /// Cancels all currently running and queued tasks.
    /// </summary>
    Task CancelAllAsync();

    /// <summary>
    /// Stops accepting new tasks. Active tasks continue to run.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// A prioritized task scheduler that manages concurrent execution of asynchronous tasks
/// with support for cancellation, priority levels, and completion events.
/// Implements both synchronous and asynchronous disposal.
/// </summary>
public sealed class PriorityTaskScheduler : IPriorityTaskScheduler, IDisposable, IAsyncDisposable
{
    private readonly Channel<Func<Task>>[] _queues =
    [
        Channel.CreateUnbounded<Func<Task>>(),
        Channel.CreateUnbounded<Func<Task>>(),
        Channel.CreateUnbounded<Func<Task>>()
    ];

    private readonly ConcurrentDictionary<int, CancellationTokenSource> _taskCtsMap = new();

    private int _maxConcurrency = Environment.ProcessorCount;
    private int _taskIdCounter = 0;
    private int _activeTasksCounter = 0;
    private bool _disposed = false;
    private volatile bool _isEnabled = true;
    private readonly Lazy<Task> _processingTask;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ILogger<PriorityTaskScheduler>? _logger;

    public PriorityTaskScheduler(ILogger<PriorityTaskScheduler>? logger = null)
    {
        _logger = logger;
        _processingTask = new Lazy<Task>(ProcessQueue);
    }

    public event EventHandler? AllTasksCompleted;
    public event EventHandler<int>? TaskCancelled;
    public event EventHandler<(Exception ex, int taskId)>? UnobservedException;


    public bool IsEnabled => _isEnabled;

    public int ActiveTasks => _activeTasksCounter;

    public int QueuedTasks => _queues.Select(q => q.Reader.Count).Sum();

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

    public async ValueTask<int> Enqueue(
        Func<CancellationToken, Task> taskFunc,
        TaskPriorityLevel priority = TaskPriorityLevel.Normal,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isEnabled) throw new InvalidOperationException("Task scheduler has been stopped.");

        var taskId = GetTaskId();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);

        if (!_taskCtsMap.TryAdd(taskId, linkedCts))
        {
            linkedCts.Dispose();
            return -1;
        }


        var taskWrapper = CreateTaskWrapper(taskId, taskFunc, linkedCts.Token);

        var writer = _queues[(int)priority].Writer;
        await writer.WriteAsync(taskWrapper, cancellationToken);

        _ = _processingTask.Value;

        return taskId;
    }

    private int GetTaskId() => Interlocked.Increment(ref _taskIdCounter);

    public async ValueTask<bool> CancelTaskAsync(int taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_taskCtsMap.TryRemove(taskId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            OnTaskCancelled(taskId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stops accepting new tasks. Active tasks will continue to run.
    /// Can be followed by WaitForCompletionAsync to wait for running tasks.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isEnabled || _disposed) return;

        _isEnabled = false;
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
    }

    private Func<Task> CreateTaskWrapper(int taskId, Func<CancellationToken, Task> userTaskFunc, CancellationToken ct)
    {
        return async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                await userTaskFunc(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                OnTaskCancelled(taskId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OnUnobservedException(ex, taskId);
            }
            finally
            {
                CleanupTask(taskId);
                TryRaiseAllTasksCompleted();
            }
        };
    }

    private Task ProcessQueue()
    {
        return Task.Run(async () =>
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                while (_isEnabled && _activeTasksCounter < _maxConcurrency && TryDequeue(out var taskFunc))
                {
                    Interlocked.Increment(ref _activeTasksCounter);
                    var _ = taskFunc().ContinueWith(_ =>
                    {
                        Interlocked.Decrement(ref _activeTasksCounter);
                    }, TaskScheduler.Default);
                }

                if (GetQueuesEmpty())
                {
                    await Task.Delay(1000, _shutdownCts.Token);
                }
            }
        });
    }

    private bool TryDequeue(out Func<Task> taskFunc)
    {
        taskFunc = null!;

        for (int i = 0; i < _queues.Length; i++)
        {
            var reader = _queues[i].Reader;
            if (reader.TryRead(out var item))
            {
                taskFunc = item;
                return true;
            }
        }

        return false;
    }

    private void CleanupTask(int taskId)
    {
        if (_taskCtsMap.TryRemove(taskId, out var cts))
            cts.Dispose();
    }

    private void TryRaiseAllTasksCompleted()
    {
        if (_disposed || !_isEnabled) return;

        bool allQueuesEmpty = GetQueuesEmpty();
        if (allQueuesEmpty)
        {
            AllTasksCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool GetQueuesEmpty()
    {
        return _queues.All(q => !q.Reader.TryPeek(out _));
    }

    private void OnTaskCancelled(int taskId) => TaskCancelled?.Invoke(this, taskId);

    private void OnUnobservedException(Exception ex, int taskId) => UnobservedException?.Invoke(this, (ex, taskId));

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during synchronous disposal of PriorityTaskScheduler.");
        }
        finally
        {
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _isEnabled = false;
        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        if (_processingTask.IsValueCreated)
        {
            var delayTask = Task.Delay(3000);
            var completed = await Task.WhenAny(_processingTask.Value, delayTask).ConfigureAwait(false);

            if (completed != _processingTask.Value)
            {
                _logger?.LogWarning("Processing task did not complete within timeout.");
            }
        }

        foreach (var (_, cts) in _taskCtsMap)
        {
            cts.Dispose();
        }


        _taskCtsMap.Clear();
        _shutdownCts.Dispose();

        _disposed = true;
    }


    public async Task CancelAllAsync()
    {
        foreach (var (_, cts) in _taskCtsMap)
        {
            if (cts != null)
            {
                await cts.CancelAsync();
                //cts.Dispose();
            }
        }
        //_taskCtsMap.Clear();
    }
}