using System.Collections.Concurrent;

namespace Sa.Utils.WorkQueue.Tests;

public sealed class WorkQueueEnqueueStrategyTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class CountingProcessor : ISaWork<int>
    {
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public Task Execute(int input, CancellationToken ct)
        {
            Interlocked.Increment(ref _processed);
            return Task.CompletedTask;
        }
    }

    private sealed class GateProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public async Task Execute(int input, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            Interlocked.Increment(ref _processed);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken ct, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Condition was not met in time");
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public void Default_Strategy_IsWait()
    {
        var options = SaWorkQueueOptions<int>.Create(new CountingProcessor());
        Assert.Equal(SaEnqueueStrategy.Wait, options.EnqueueStrategy);
    }

    [Fact]
    public async Task Wait_Strategy_BlocksWhenFull_ThenAccepts()
    {
        var processor = new GateProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(1)
                .WithEnqueueStrategy(SaEnqueueStrategy.Wait));

        Assert.True(await queue.Enqueue(1, TestToken));

        // Reader has picked the item up: the buffer is empty again.
        await WaitUntil(() => queue.AvailableCapacity == queue.QueueCapacity, TestToken);

        Assert.True(await queue.Enqueue(2, TestToken)); // buffer: 1 = full

        var pending = queue.Enqueue(3, TestToken); // buffer full -> blocks
        await Task.Delay(100, TestToken);
        Assert.False(pending.IsCompleted, "Wait strategy must block while the buffer is full");

        processor.Gate.TrySetResult();

        Assert.True(await pending);

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
    }

    [Fact]
    public async Task Skip_Strategy_ReturnsFalseWhenFull_AndDoesNotCountDroppedItem()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip));

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.True(await queue.Enqueue(2, TestToken));
        Assert.False(await queue.Enqueue(3, TestToken));

        Assert.Equal(2, queue.QueueTasks); // dropped item is not counted
        Assert.Equal(0, queue.AvailableCapacity);

        queue.ConcurrencyLimit = 1; // resume: readers drain the buffer
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);

        Assert.True(await queue.Enqueue(3, TestToken)); // buffer free again
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
    }

    [Fact]
    public async Task Skip_Strategy_PausedAtRuntime_FillsThenSkips()
    {
        var processor = new GateProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip));

        Assert.True(await queue.Enqueue(1, TestToken));

        await WaitUntil(() => queue.AvailableCapacity == queue.QueueCapacity, TestToken); // reader holds item 1 on the gate
        Assert.True(await queue.Enqueue(2, TestToken)); // buffer: 1

        queue.ConcurrencyLimit = 0; // pause (hard cancel: item 1 is dropped)

        Assert.True(await queue.Enqueue(3, TestToken)); // buffer: 2
        Assert.False(await queue.Enqueue(4, TestToken)); // buffer full

        queue.ConcurrencyLimit = 1; // resume
        processor.Gate.TrySetResult();

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed); // items 2 and 3 were processed, item 1 was cancelled
    }

    [Fact]
    public async Task Throw_Strategy_ThrowsWhenFull()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Throw));

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.True(await queue.Enqueue(2, TestToken));

        await Assert.ThrowsAsync<SaWorkQueueFullException>(async () =>
        {
            await queue.Enqueue(3, cancellationToken: CancellationToken.None);
        });

        queue.ConcurrencyLimit = 1; // resume: readers drain the buffer
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);

        Assert.True(await queue.Enqueue(3, TestToken));
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
    }

    [Fact]
    public async Task Skip_Strategy_DroppedItem_IsReportedAsSkipped()
    {
        var processor = new CountingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.True(await queue.Enqueue(2, TestToken));
        Assert.False(await queue.Enqueue(3, TestToken)); // buffer full: dropped

        Assert.Contains(statuses, s => s.Item == 3 && s.Status == SaWorkStatus.Skipped);
        Assert.DoesNotContain(statuses, s => s.Item == 3 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);

        queue.ConcurrencyLimit = 1; // resume: readers drain the buffer
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed); // dropped item was never processed
        Assert.Contains(statuses, s => s.Item == 1 && s.Status == SaWorkStatus.Completed);
        Assert.Contains(statuses, s => s.Item == 2 && s.Status == SaWorkStatus.Completed);
        Assert.DoesNotContain(statuses, s => s.Item == 3 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);
    }

    [Fact]
    public async Task Throw_Strategy_ExceptionCarriesQueueAndItemInfo()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Throw)
                .WithItemDisplayName(item => $"order-{item}"));

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.True(await queue.Enqueue(2, TestToken));

        var ex = await Record.ExceptionAsync(async () =>
            await queue.Enqueue(3, cancellationToken: CancellationToken.None));

        Assert.NotNull(ex);
        Assert.IsType<SaWorkQueueFullException>(ex!);
        var qex = (SaWorkQueueFullException)ex!;
        Assert.Equal(2, qex.QueueCapacity);
        Assert.Equal(2, qex.QueuedCount);
        Assert.Equal("order-3", qex.ItemDisplayName);
        Assert.Null(qex.AcceptedCount); // single-item call: no batch counters
        Assert.Null(qex.TotalCount);
        Assert.Contains("2/2", qex.Message);
        Assert.Contains("order-3", qex.Message);
    }

    [Fact]
    public async Task Throw_Strategy_FailedEnqueue_DoesNotCorruptTaskCount()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Throw));

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.True(await queue.Enqueue(2, TestToken));
        Assert.Equal(2, queue.QueueTasks);

        await Assert.ThrowsAsync<SaWorkQueueFullException>(async () =>
        {
            await queue.Enqueue(3, cancellationToken: CancellationToken.None);
        });

        // Regression: the failed enqueue must balance exactly the one active mark
        // it took — the marks of the accepted items must survive.
        Assert.Equal(2, queue.QueueTasks);

        queue.ConcurrencyLimit = 1; // resume
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);
        Assert.Equal(0, queue.QueueTasks);
        Assert.True(queue.IsIdle());
    }

    [Theory]
    [InlineData(SaEnqueueStrategy.Wait)]
    [InlineData(SaEnqueueStrategy.Skip)]
    [InlineData(SaEnqueueStrategy.Throw)]
    public async Task Stopped_Queue_ThrowsInAllStrategies(SaEnqueueStrategy strategy)
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(strategy));

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        queue.Shutdown();

        // Enqueue is async: the exception is carried by the returned ValueTask,
        // so it must be awaited to be observed.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.Enqueue(2, cancellationToken: CancellationToken.None);
        });
        queue.Dispose();
    }

    [Theory]
    [InlineData(SaEnqueueStrategy.Wait)]
    [InlineData(SaEnqueueStrategy.Skip)]
    [InlineData(SaEnqueueStrategy.Throw)]
    public async Task Disposed_Queue_ThrowsInAllStrategies(SaEnqueueStrategy strategy)
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(strategy));

        queue.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await queue.Enqueue(1, cancellationToken: CancellationToken.None);
        });
    }

    [Fact]
    public async Task AvailableCapacity_TracksBufferOccupancy()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(3));

        Assert.Equal(3, queue.AvailableCapacity);

        Assert.True(await queue.Enqueue(1, TestToken));
        Assert.Equal(2, queue.AvailableCapacity);

        Assert.True(await queue.Enqueue(2, TestToken));
        Assert.Equal(1, queue.AvailableCapacity);

        Assert.True(await queue.Enqueue(3, TestToken));
        Assert.Equal(0, queue.AvailableCapacity);

        queue.ConcurrencyLimit = 1; // readers drain the buffer
        await WaitUntil(() => queue.AvailableCapacity == queue.QueueCapacity, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
    }
}
