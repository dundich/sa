using Sa.Classes;
using System.Collections.Concurrent;

namespace SaTests.Classes;


internal sealed class TrackingWork(
    Func<BlockingTaskModel, CancellationToken, Task>? executeFunc = null,
    Action<WorkInfo>? onStatusChanged = null) : IWork<BlockingTaskModel>, IWorkObserver<BlockingTaskModel>
{
    private readonly Func<BlockingTaskModel, CancellationToken, Task>? _executeFunc = executeFunc;
    private readonly Action<WorkInfo>? _onStatusChanged = onStatusChanged;

    // Thread-safe
    private int _maxParallelObserved;
    private int _currentParallel;
    private long _completedCount;
    private long _failedCount;
    private long _cancelledCount;

    public int MaxParallelObserved => Volatile.Read(ref _maxParallelObserved);
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public long FailedCount => Interlocked.Read(ref _failedCount);
    public long CancelledCount => Interlocked.Read(ref _cancelledCount);
    public long TotalProcessed => CompletedCount + FailedCount + CancelledCount;

    public async Task Execute(BlockingTaskModel model, CancellationToken cancellationToken)
    {
        int current = Interlocked.Increment(ref _currentParallel);
        try
        {
            int max = Volatile.Read(ref _maxParallelObserved);
            if (current > max)
            {
                Interlocked.CompareExchange(ref _maxParallelObserved, current, max);
            }

            await model.AllowCompletion.Task;


            if (_executeFunc != null)
                await _executeFunc(model, cancellationToken);
            else
                await Task.Delay(50, cancellationToken); // Default delay
        }
        finally
        {
            Interlocked.Decrement(ref _currentParallel);
        }
    }

    public Task HandleChanges(
        BlockingTaskModel model,
        WorkInfo work,
        CancellationToken cancellationToken)
    {
        _onStatusChanged?.Invoke(work);

        switch (work.Status)
        {
            case WorkStatus.Completed:
                Interlocked.Increment(ref _completedCount);
                break;
            case WorkStatus.Faulted:
                Interlocked.Increment(ref _failedCount);
                break;
            case WorkStatus.Cancelled:
                Interlocked.Increment(ref _cancelledCount);
                break;
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        Volatile.Write(ref _maxParallelObserved, 0);
        Volatile.Write(ref _currentParallel, 0);
        Interlocked.Exchange(ref _completedCount, 0);
        Interlocked.Exchange(ref _failedCount, 0);
        Interlocked.Exchange(ref _cancelledCount, 0);
    }
}

internal sealed record BlockingTaskModel(
    TimeSpan? Delay = null,
    bool ShouldFail = false,
    string? Id = null
)
{
    public TaskCompletionSource AllowCompletion { get; }
        = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public BlockingTaskModel Unlock()
    {
        AllowCompletion.SetResult();
        return this;
    }
};


public sealed class WorkQueueConcurrencyTests : IAsyncLifetime
{
    private readonly List<IDisposable> _disposables = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            if (d is IAsyncDisposable ad)
                await ad.DisposeAsync();
            else
                d.Dispose();
        }
        _disposables.Clear();
    }

    private WorkQueue<BlockingTaskModel> CreateQueue(
        TrackingWork work,
        int concurrencyLimit,
        int? queueCapacity = null,
        int? maxConcurrency = null)
    {
        var options = new WorkQueueOptions<BlockingTaskModel>(
            Processor: work,
            ConcurrencyLimit: concurrencyLimit,
            MaxConcurrency: maxConcurrency ?? concurrencyLimit * 2,
            QueueCapacity: queueCapacity ?? 100
        );

        var queue = new WorkQueue<BlockingTaskModel>(options);
        _disposables.Add(queue);
        return queue;
    }



    [Fact]
    public async Task ConcurrencyLimit_Respected_AtStartup()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 3);

        var blockers = new List<TaskCompletionSource>();

        // Act: enqueue 10 tasks that block until we allow them
        for (int i = 0; i < 10; i++)
        {
            var model = new BlockingTaskModel();
            blockers.Add(model.AllowCompletion);
            await queue.Enqueue(model, TestContext.Current.CancellationToken);
        }

        await Task.Delay(200, TestContext.Current.CancellationToken);


        Assert.True(work.MaxParallelObserved <= 3,
            $"Expected max parallel <= 3, but observed {work.MaxParallelObserved}");

        Assert.True(work.MaxParallelObserved >= 1, "At least one task should have started");


        foreach (var tcs in blockers)
            tcs.SetResult();

        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, work.TotalProcessed);
    }

    [Fact]
    public async Task ConcurrencyLimit_Zero_StopsProcessing()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 0);

        // Enqueue tasks
        for (int i = 0; i < 5; i++)
        {
            var model = new BlockingTaskModel(Delay: TimeSpan.FromMilliseconds(10));
            await queue.Enqueue(model, TestContext.Current.CancellationToken);
            model.Unlock(); // Сразу разрешаем завершение
        }

        Assert.Equal(5, queue.QueueTasks + work.TotalProcessed);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(5, queue.QueueTasks + work.TotalProcessed);
        Assert.Equal(0, work.TotalProcessed);

        // Восстанавливаем лимит и ждём завершения
        queue.ConcurrencyLimit = 2;
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, work.TotalProcessed);
    }

    [Fact]
    public async Task ConcurrencyLimit_DynamicIncrease_Works()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 2);

        var phase1Blockers = new List<TaskCompletionSource>();
        var phase2Blockers = new List<TaskCompletionSource>();

        // Act Phase 1: enqueue 4 tasks with limit=2
        for (int i = 0; i < 4; i++)
        {
            var model = new BlockingTaskModel(Id: $"P1-{i}");
            phase1Blockers.Add(model.AllowCompletion);
            await queue.Enqueue(model, TestContext.Current.CancellationToken);
        }
        await Task.Delay(150, TestContext.Current.CancellationToken);

        int maxAtPhase1 = work.MaxParallelObserved;
        Assert.True(maxAtPhase1 <= 2, $"Phase 1: expected <=2, got {maxAtPhase1}");

        // Act Phase 2: increase limit to 4 and enqueue more
        queue.ConcurrencyLimit = 4;

        for (int i = 0; i < 4; i++)
        {
            var model2 = new BlockingTaskModel(Id: $"P2-{i}");
            phase2Blockers.Add(model2.AllowCompletion);
            await queue.Enqueue(model2, TestContext.Current.CancellationToken);
        }
        await Task.Delay(150, TestContext.Current.CancellationToken);

        // Assert: после увеличения лимита должно быть возможно до 4 параллельных
        Assert.True(work.MaxParallelObserved <= 4,
            $"Expected max parallel <= 4, but observed {work.MaxParallelObserved}");
        Assert.True(work.MaxParallelObserved > maxAtPhase1,
            "Max parallel should have increased after raising limit");

        // Завершаем все задачи
        foreach (var tcs in phase1Blockers.Concat(phase2Blockers))
            tcs.SetResult();

        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, work.TotalProcessed);
    }

    [Fact]
    public async Task ConcurrencyLimit_DynamicDecrease_ThrottlesReaders()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 4);

        var blockers = new List<TaskCompletionSource>();
        var startedEvents = new List<TaskCompletionSource>();

        // Act: enqueue 8 tasks
        for (int i = 0; i < 8; i++)
        {
            var model = new BlockingTaskModel(Id: $"T-{i}");
            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            blockers.Add(model.AllowCompletion);
            startedEvents.Add(startedTcs);

            await queue.Enqueue(model, TestContext.Current.CancellationToken);
        }


        for (int i = 0; i < 4; i++)
            blockers[i].SetResult();

        await Task.Delay(200, TestContext.Current.CancellationToken);
        int beforeDecrease = work.MaxParallelObserved;
        Assert.True(beforeDecrease <= 4, $"Before decrease: expected <=4, got {beforeDecrease}");

        Assert.Equal(4, queue.QueueTasks);

        // Act: decrease limit to 2
        queue.ConcurrencyLimit = 2;
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.True(work.MaxParallelObserved <= 4,
            $"After decrease: observed spike to {work.MaxParallelObserved}");

        // Завершаем остальные
        for (int i = 4; i < 8; i++)
            blockers[i].SetResult();


        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8, work.TotalProcessed);
        Assert.Equal(0, queue.QueueTasks);
    }



    [Theory]
    [InlineData(1, 10)]
    [InlineData(5, 25)]
    [InlineData(10, 50)]
    public async Task ConcurrencyLimit_NeverExceeded_UnderLoad(int limit, int taskCount)
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: limit, maxConcurrency: limit * 2);

        var blockers = new List<TaskCompletionSource>();

        // Act: rapid enqueue
        var enqueueTasks = Enumerable.Range(0, taskCount).Select(async i =>
        {
            var model = new BlockingTaskModel(Delay: TimeSpan.FromMilliseconds(20));
            blockers.Add(model.AllowCompletion);
            await queue.Enqueue(model);
        });

        await Task.WhenAll(enqueueTasks);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(work.MaxParallelObserved <= limit);

        foreach (var tcs in blockers)
            tcs.SetResult();

        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(taskCount, work.TotalProcessed);
    }

    [Fact]
    public async Task ConcurrencyLimit_RapidChanges_DoesNotBreak()
    {
        int totals = 30;
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 3, maxConcurrency: 6, queueCapacity: totals);

        using var cts = new CancellationTokenSource();
        var blockers = new ConcurrentQueue<TaskCompletionSource>();

        // Background enqueuer
        var enqueueTask = Task.Run(async () =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested && i < totals)
            {
                var model = new BlockingTaskModel(Id: $"T-{i++}");
                blockers.Enqueue(model.AllowCompletion);
                await queue.Enqueue(model, cts.Token);
                await Task.Delay(20, cts.Token);
            }
        }, cts.Token);

        // Limiter changer
        var changeTask = Task.Run(async () =>
        {
            int[] limits = [2, 5, 1, 4, 3, 6, 2];
            foreach (var limit in limits)
            {
                if (cts.Token.IsCancellationRequested) break;
                queue.ConcurrencyLimit = limit;
                await Task.Delay(100, cts.Token);
            }
            // cts.Cancel(); // Stop enqueuer
        }, cts.Token);

        // Processor: complete tasks with small delay
        var processTask = Task.Run(async () =>
        {

            int i = 0;

            try
            {
                while (!cts.Token.IsCancellationRequested || !blockers.IsEmpty)
                {
                    if (blockers.TryDequeue(out var tcs))
                    {
                        i++;
                        await Task.Delay(10);

                        tcs.TrySetResult();
                    }
                    else
                    {
                        await Task.Delay(100, cts.Token);
                        if (i >= totals)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }, cts.Token);

        await Task.WhenAll(enqueueTask, changeTask, processTask);
        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        // Assert: no exceptions, all tasks processed, limit respected at peaks
        Assert.True(work.MaxParallelObserved <= 6,
            $"Max observed {work.MaxParallelObserved} exceeded max configured limit 6");

        Assert.Equal(totals, work.TotalProcessed); // All accounted for
    }

    [Fact]
    public async Task ConcurrencyLimit_SetToMaxAllowed_ClampsCorrectly()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 2, maxConcurrency: 5);

        // Act & Assert
        queue.ConcurrencyLimit = 10; // Above max
        Assert.Equal(5, queue.ConcurrencyLimit); // Should be clamped

        queue.ConcurrencyLimit = -1; // Below min
        Assert.Equal(0, queue.ConcurrencyLimit); // Should be clamped to 0

        queue.ConcurrencyLimit = 3; // Valid
        Assert.Equal(3, queue.ConcurrencyLimit);
    }

    // ─────────────────────────────────────────────────────────────
    // 🔹 Тесты на корректность обработки после изменений лимита
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrencyLimit_Changes_DontLoseTasks()
    {
        // Arrange
        var completedIds = new ConcurrentBag<string>();
        var work = new TrackingWork(
            executeFunc: async (model, ct) =>
            {
                await Task.Delay(10, ct);
                if (!string.IsNullOrEmpty(model.Id))
                    completedIds.Add(model.Id);
            });

        var queue = CreateQueue(work, concurrencyLimit: 2);


        List<BlockingTaskModel> models = [];

        // Act: enqueue with interleaved limit changes
        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            if (i == 5) queue.ConcurrencyLimit = 4;
            if (i == 10) queue.ConcurrencyLimit = 1;
            if (i == 15) queue.ConcurrencyLimit = 3;

            var model = new BlockingTaskModel(Id: $"T-{i:D3}");
            models.Add(model);
            var t = queue.Enqueue(model, TestContext.Current.CancellationToken).AsTask();

            tasks.Add(t);
        }

        await Task.WhenAll(tasks);

        foreach (var model in models) model.Unlock();


        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        // Assert: all tasks completed, no duplicates, no losses
        Assert.Equal(20, completedIds.Count);
        Assert.Equal(20, completedIds.Distinct().Count());
        Assert.All(Enumerable.Range(0, 20), i =>
            Assert.Contains($"T-{i:D3}", completedIds));
    }

    [Fact]
    public async Task ConcurrencyLimit_WithCancellation_HandlesGracefully()
    {
        // Arrange
        var work = new TrackingWork();
        var queue = CreateQueue(work, concurrencyLimit: 3);

        using var cts = new CancellationTokenSource();

        // Act: enqueue cancellable tasks
        var enqueueTasks = Enumerable.Range(0, 10).Select(i =>
        {
            var model = new BlockingTaskModel(Delay: TimeSpan.FromSeconds(20));
            model.Unlock();
            return queue.Enqueue(model, cts.Token).AsTask();
        });

        await Task.WhenAll(enqueueTasks);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        await cts.CancelAsync();

        // Change limit during cancellation
        queue.ConcurrencyLimit = 1;
        queue.ConcurrencyLimit = 5;

        // Shutdown should complete without hanging
        await queue.ShutdownAsync();

        await queue.WaitForIdleAsync(TestContext.Current.CancellationToken);

        Assert.True(work.TotalProcessed > 0);
    }
}

