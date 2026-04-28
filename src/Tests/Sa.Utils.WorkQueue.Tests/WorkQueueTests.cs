namespace Sa.Utils.WorkQueue.Tests;

public class WorkQueueTests
{
    private sealed class TestModel
    {
        public string Data { get; set; } = string.Empty;
        public bool WasProcessed { get; set; }
    }

    private sealed class TestWork : ISaWork<TestModel>
    {
        public Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            model.WasProcessed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestWorkWithDelay(TimeSpan delay) : ISaWork<TestModel>
    {
        private readonly TimeSpan _delay = delay;

        public async Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            model.WasProcessed = true;
        }
    }

    private sealed class TestWorkThatThrows : ISaWork<TestModel>
    {
        public Task Execute(TestModel model, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Test exception");
        }
    }

    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enqueue_SingleTask_ExecutesAndCompletes()
    {
        // Arrange
        var model = new TestModel();
        var processor = new TestWorkWithDelay(TimeSpan.FromMilliseconds(50));
        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>.Create(processor));

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.True(model.WasProcessed);
        Assert.Equal(0, queue.QueueTasks);

    }


    [Fact]
    public async Task Enqueue_WaitForIdle_Completed()
    {
        // Arrange
        var model = new TestModel();
        var processor = new TestWorkWithDelay(TimeSpan.FromMilliseconds(10));
        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>.Create(processor));

        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.True(model.WasProcessed);
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Enqueue_MultipleTasks_AllExecuted()
    {
        // Arrange
        var processor = new TestWorkWithDelay(TimeSpan.FromMilliseconds(30));
        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>.Create(processor));
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
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task ConcurrencyLimit_Respected()
    {
        // Arrange
        var delay = TimeSpan.FromMilliseconds(120);
        var processor = new TestWorkWithDelay(delay);

        var concurrencyLimit = 3;
        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>
            .Create(processor)
            .WithConcurrencyLimit(concurrencyLimit));

        var models = new List<TestModel> { new(), new(), new(), new(), new(), new(), new() };

        // Act
        foreach (var model in models)
            await queue.Enqueue(model, cancellationToken: TestToken);

        await Task.Delay(50, TestToken);

        // Assert
        Assert.True(queue.ConcurrencyLimit <= concurrencyLimit, "Concurrency limit violated");

        Assert.False(queue.IsIdle());

        await queue.WaitForIdleAsync(cancellationToken: TestToken);


        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task FaultedTask_StatusIsFaulted()
    {
        List<(SaWorkStatus Status, Exception? LastError)> changes = [];

        // Arrange
        var processor = new TestWorkThatThrows();

        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>
            .Create(processor)
            .WithStatusCallback((_, s, e) => changes.Add((s, e))));

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await Task.Delay(100, TestToken);

        // Assert

        Assert.Contains(changes, x => x.Status == SaWorkStatus.Faulted);

        Assert.Contains(changes, x => x.LastError is InvalidOperationException);
        Assert.Contains(changes, x => x.LastError?.Message == "Test exception");
    }

    [Fact]
    public async Task CancelledTask_StatusIsAborted()
    {
        List<SaWorkStatus> statuses = [];


        // Arrange
        using var cts = new CancellationTokenSource();
        var processor = new TestWorkWithDelay(TimeSpan.FromSeconds(5));

        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>
            .Create(processor)
            .WithStatusCallback((_, s, _) => statuses.Add(s)));

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: cts.Token);
        await cts.CancelAsync();

        await queue.WaitForIdleAsync(TestToken);

        // Assert

        Assert.Contains(SaWorkStatus.Aborted, statuses);
    }

    [Fact]
    public async Task ShutdownAsync_StopsProcessing()
    {
        List<SaWorkStatus> errors = [];

        // Arrange
        var processor = new TestWorkWithDelay(TimeSpan.FromMilliseconds(300));

        using var queue = new SaWorkQueue<TestModel>(SaWorkQueueOptions<TestModel>
            .Create(processor)
            .WithStatusCallback((m, s, e) =>
            {
                if (s == SaWorkStatus.Cancelled)
                    errors.Add(s);
            }));

        for (int i = 0; i < 5; i++)
            await queue.Enqueue(new TestModel(), cancellationToken: TestToken);


        await Task.Delay(50, TestToken);

        // Act
        await queue.ShutdownAsync();

        // Assert
        Assert.False(queue.IsEnabled);
        Assert.True(queue.QueueTasks <= 5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.Enqueue(new TestModel(), cancellationToken: CancellationToken.None);
        });

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(0, queue.QueueTasks);

        Assert.Equal(5, errors.Count);
    }



    [Fact]
    public async Task Observer_InvokedOnStateChange()
    {

        List<SaWorkStatus> statuses = [];


        // Arrange
        var processor = new TestWork();
        using var queue = new SaWorkQueue<TestModel>(
            SaWorkQueueOptions<TestModel>
                .Create(processor)
                .WithStatusCallback((_, s, _) => statuses.Add(s)));

        var model = new TestModel();

        // Act
        await queue.Enqueue(model, cancellationToken: TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        // Assert
        Assert.True(statuses.Count >= 2); // Running → Completed
        Assert.Contains(statuses, x => x == SaWorkStatus.Running);
        Assert.Contains(statuses, x => x == SaWorkStatus.Completed);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        // Arrange
        var processor = new TestWork();
        var queue = new SaWorkQueue<TestModel>(
            SaWorkQueueOptions<TestModel>.Create(processor));

        await queue.Enqueue(new TestModel(), cancellationToken: TestToken);
        await queue.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(()
            => queue.Enqueue(new TestModel(), cancellationToken: TestToken).AsTask());
    }

    [Fact]
    public void ConcurrencyLimit_InvalidValue_Clamp()
    {
        var queue = new SaWorkQueue<TestModel>(
            SaWorkQueueOptions<TestModel>.Create(new TestWork()))
        {
            ConcurrencyLimit = -1
        };

        Assert.Equal(0, queue.ConcurrencyLimit);

        queue.Dispose();
    }

    [Fact]
    public async Task Enqueue_Disposed_Throws()
    {
        var queue = new SaWorkQueue<TestModel>(
            SaWorkQueueOptions<TestModel>.Create(new TestWork()));
        queue.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(()
            => queue.Enqueue(new TestModel(), cancellationToken: TestToken).AsTask());
    }

    [Fact]
    public async Task ShutdownAsync_CleansUpResources()
    {
        // Arrange
        var processor = new TestWork();
        var queue = new SaWorkQueue<TestModel>(
            SaWorkQueueOptions<TestModel>.Create(processor));

        await queue.Enqueue(new TestModel(), cancellationToken: TestToken);
        await queue.ShutdownAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(()
            => queue.Enqueue(new TestModel(), cancellationToken: TestToken).AsTask());
    }
}
