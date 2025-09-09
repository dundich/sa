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
    bool IsEnabled { get; }
    int ActiveTasks { get; }
    int QueuedTasks { get; }
    int ConcurrencyLimit { get; set; }

    event EventHandler? AllTasksCompleted;
    event EventHandler<int>? TaskCancelled;
    event EventHandler<(Exception ex, int taskId)>? UnobservedException;

    ValueTask<int> Enqueue(
        Func<CancellationToken, Task> taskFunc,
        TaskPriorityLevel priority = TaskPriorityLevel.Normal,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CancelTaskAsync(int taskId);
    Task CancelAllAsync();
    Task StopAsync();
    Task WaitForCompletionAsync(CancellationToken ct = default);
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
        await writer.WriteAsync(taskWrapper, cancellationToken).ConfigureAwait(false);

        // Запускаем обработку, если ещё не запущена
        _ = _processingTask.Value;

        return taskId;
    }

    private int GetTaskId() => Interlocked.Increment(ref _taskIdCounter);

    public async ValueTask<bool> CancelTaskAsync(int taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_taskCtsMap.TryRemove(taskId, out var cts))
            return false;

        await cts.CancelAsync().ConfigureAwait(false);
        cts.Dispose();

        OnTaskCancelled(taskId);
        return true;
    }

    public async Task StopAsync()
    {
        if (!_isEnabled || _disposed) return;

        _isEnabled = false;
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
    }

    public async Task WaitForCompletionAsync(CancellationToken ct = default)
    {
        while (_activeTasksCounter > 0 || !GetQueuesEmpty())
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private Func<Task> CreateTaskWrapper(int taskId, Func<CancellationToken, Task> userTaskFunc, CancellationToken ct)
    {
        return async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // Проверяем, не была ли задача отменена до запуска
                if (!_taskCtsMap.ContainsKey(taskId))
                    return;

                await userTaskFunc(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException cex) when (cex.CancellationToken == ct)
            {
                OnTaskCancelled(taskId);
            }
            catch (Exception ex)
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

    private async Task ProcessQueue()
    {
        var ct = _shutdownCts.Token;
        var waitTasks = _queues
            .Select(q => ValueTask.ToTask(q.Reader.WaitToReadAsync(ct)))
            .ToArray();

        while (!ct.IsCancellationRequested)
        {
            // Запускаем задачи, если есть место
            while (_isEnabled && _activeTasksCounter < _maxConcurrency && TryDequeue(out var taskFunc))
            {
                Interlocked.Increment(ref _activeTasksCounter);
                _ = taskFunc().ContinueWith(
                    _ => Interlocked.Decrement(ref _activeTasksCounter),
                    ct,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                );
            }

            // Если очереди пусты — ждём новую задачу
            if (GetQueuesEmpty())
            {
                try
                {
                    await Task.WhenAny(waitTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }

                // Пересоздаём завершённые WaitToReadAsync
                for (int i = 0; i < waitTasks.Length; i++)
                {
                    if (waitTasks[i].IsCompleted)
                    {
                        waitTasks[i] = ValueTask.ToTask(_queues[i].Reader.WaitToReadAsync(ct));
                    }
                }
            }
            else
            {
                // Есть задачи, но нет concurrency — небольшая пауза
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }
    }

    private bool TryDequeue(out Func<Task> taskFunc)
    {
        taskFunc = null!;
        for (int i = 0; i < _queues.Length; i++)
        {
            if (_queues[i].Reader.TryRead(out taskFunc!))
                return true;
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

        if (GetQueuesEmpty() && _activeTasksCounter == 0)
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
            // Запускаем асинхронную очистку в фоновом потоке
            Task.Run(async () => await DisposeAsync().AsTask())
                .Wait(TimeSpan.FromSeconds(5));
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
            var completed = await Task.WhenAny(_processingTask.Value, Task.Delay(3000)).ConfigureAwait(false);
            if (completed != _processingTask.Value)
            {
                _logger?.LogWarning("Processing task did not complete within timeout.");
            }
        }

        // Отменяем и освобождаем все CTS
        foreach (var (_, cts) in _taskCtsMap)
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        _taskCtsMap.Clear();

        _shutdownCts.Dispose();
        _disposed = true;
    }

    public async Task CancelAllAsync()
    {
        var cancellationTasks = _taskCtsMap.Values
            .Select(cts => cts.CancelAsync())
            .ToArray();

        await Task.WhenAll(cancellationTasks).ConfigureAwait(false);
    }
}