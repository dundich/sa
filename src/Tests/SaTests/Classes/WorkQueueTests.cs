using Sa.Classes;

namespace SaTests.Classes;

public class WorkQueueTests
{
    private sealed class TestModel
    {
        public string Data { get; set; } = string.Empty;
        public bool WasProcessed { get; set; }
    }

    private sealed class TestWork : IWork<TestModel>
    {
        public Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            model.WasProcessed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestWorkWithDelay(TimeSpan delay) : IWork<TestModel>
    {
        private readonly TimeSpan _delay = delay;

        public async Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            model.WasProcessed = true;
        }
    }

    private sealed class TestWorkThatThrows : IWork<TestModel>
    {
        public Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
        }
    }

    private sealed class TestObserver : IWorkObserver<TestModel>
    {
        public readonly List<WorkInfo> Changes = [];

        public Task HandleChanges(TestModel model, WorkInfo work, CancellationToken cancellationToken)
        {
            lock (Changes)
            {
                Changes.Add(work);
            }
            return Task.CompletedTask;
        }
    }

    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enqueue_SingleTask_ExecutesAndCompletes()
    {
        // Arrange
        var model = new TestModel();
        var processor = new TestWork();
        using var queue = new WorkQueue<TestModel>(processor);

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.True(model.WasProcessed);
        Assert.Equal(0, queue.ActiveTasks);
        Assert.Equal(0, queue.QueuedTasks);
    }

    [Fact]
    public async Task Enqueue_MultipleTasks_AllExecuted()
    {
        // Arrange
        var processor = new TestWork();
        using var queue = new WorkQueue<TestModel>(processor);
        var models = new List<TestModel>
        {
            new(), new(), new()
        };

        // Act
        foreach (var model in models)
            await queue.Enqueue(model, cancellationToken: TestToken);

        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.All(models, m => Assert.True(m.WasProcessed));
        Assert.Equal(0, queue.ActiveTasks);
        Assert.Equal(0, queue.QueuedTasks);
    }

    [Fact]
    public async Task ConcurrencyLimit_Respected()
    {
        // Arrange
        var concurrencyLimit = 3;
        var delay = TimeSpan.FromMilliseconds(120);
        var processor = new TestWorkWithDelay(delay);
        using var queue = new WorkQueue<TestModel>(processor)
        {
            ConcurrencyLimit = concurrencyLimit
        };

        var models = new List<TestModel> { new(), new(), new(), new(), new(), new(), new() };

        // Act
        foreach (var model in models)
            await queue.Enqueue(model, cancellationToken: TestToken);

        await Task.Delay(50, TestToken);

        // Assert
        Assert.True(queue.ActiveTasks <= concurrencyLimit, "Concurrency limit violated");

        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(0, queue.ActiveTasks);
        Assert.Equal(0, queue.QueuedTasks);
    }

    [Fact]
    public async Task FaultedTask_StatusIsFaulted()
    {
        // Arrange
        var processor = new TestWorkThatThrows();
        var observer = new TestObserver();
        using var queue = new WorkQueue<TestModel>(processor, observer);

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await Task.Delay(100, TestToken);

        // Assert
        var completed = observer.Changes.Find(x => x.Status == WorkStatus.Faulted);

        Assert.IsType<InvalidOperationException>(completed.LastError);
        Assert.Contains("Test exception", completed.LastError?.Message);
    }

    [Fact]
    public async Task CancelledTask_StatusIsCancelled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var processor = new TestWorkWithDelay(TimeSpan.FromSeconds(5));
        var observer = new TestObserver();
        using var queue = new WorkQueue<TestModel>(processor, observer);

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: cts.Token);
        await cts.CancelAsync();

        await queue.WaitForIdleAsync(TestToken);

        // Assert
        var cancelled = observer.Changes.Find(x => x.Status == WorkStatus.Cancelled);
        Assert.NotEqual(0, cancelled.Id);
    }

    [Fact]
    public async Task ShutdownAsync_StopsProcessing()
    {
        // Arrange
        var processor = new TestWorkWithDelay(TimeSpan.FromMilliseconds(500));
        var observer = new TestObserver();
        using var queue = new WorkQueue<TestModel>(processor, observer);

        for (int i = 0; i < 5; i++)
            await queue.Enqueue(new TestModel(), cancellationToken: TestToken);

        // Act
        await queue.ShutdownAsync();

        // Assert
        Assert.False(queue.IsEnabled);
        Assert.True(queue.ActiveTasks <= 5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.Enqueue(new TestModel(), cancellationToken: CancellationToken.None);
        });

        observer.Changes.GroupBy(c => c.Id).
    }

    [Fact]
    public async Task StatusChanged_Event_FiresOnStateChange()
    {
        // Arrange
        var processor = new TestWork();
        WorkInfo? lastInfo = null;
        var eventTcs = new TaskCompletionSource<WorkInfo>();

        using var queue = new WorkQueue<TestModel>(processor);

        queue.StatusChanged += (sender, info) =>
        {
            lastInfo = info;
            if (info.Status == WorkStatus.Completed)
                eventTcs.TrySetResult(info);
        };

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        var result = await eventTcs.Task;

        // Assert
        Assert.Equal(WorkStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Observer_InvokedOnStateChange()
    {
        // Arrange
        var observer = new TestObserver();
        var processor = new TestWork();
        using var queue = new WorkQueue<TestModel>(processor, observer);

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.True(observer.Changes.Count >= 3); // Queued → Running → Completed
        Assert.Contains(observer.Changes, x => x.Status == WorkStatus.Queued);
        Assert.Contains(observer.Changes, x => x.Status == WorkStatus.Running);
        Assert.Contains(observer.Changes, x => x.Status == WorkStatus.Completed);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var processor = new TestWork();
        var queue = new WorkQueue<TestModel>(processor);

        await queue.Enqueue(new TestModel(), cancellationToken: TestToken);
        await queue.DisposeAsync();

        // Act & Assert
        Assert.False(queue.IsEnabled);
        Assert.Equal(0, queue.QueuedTasks); // Writer completed
        await Assert.ThrowsAsync<ObjectDisposedException>(()
            => queue.Enqueue(new TestModel(), cancellationToken: TestToken).AsTask());
    }

    [Fact]
    public void ConcurrencyLimit_InvalidValue_Throws()
    {
        var queue = new WorkQueue<TestModel>(new TestWork());

        Assert.Throws<ArgumentOutOfRangeException>(() => queue.ConcurrencyLimit = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.ConcurrencyLimit = -1);

        queue.Dispose();
    }

    [Fact]
    public async Task Enqueue_Disposed_Throws()
    {
        var queue = new WorkQueue<TestModel>(new TestWork());
        await queue.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.Enqueue(new TestModel(), cancellationToken: TestToken).AsTask());
    }
}
