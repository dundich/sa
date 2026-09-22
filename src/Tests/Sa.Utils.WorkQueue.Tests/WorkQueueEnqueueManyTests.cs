using System.Collections.Concurrent;

namespace Sa.Utils.WorkQueue.Tests;

public sealed class WorkQueueEnqueueManyTests
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
    public async Task Wait_Strategy_AllAccepted_WhenBufferHasRoom()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(5));

        Assert.Equal(3, await queue.EnqueueMany([1, 2, 3], TestToken));

        Assert.Equal(3, queue.QueueTasks);
        Assert.Equal(2, queue.AvailableCapacity);

        queue.ConcurrencyLimit = 1; // resume: readers drain the buffer
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Wait_Strategy_WaitsForSpace_ThenAcceptsAll()
    {
        var processor = new GateProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(1)
                .WithEnqueueStrategy(SaEnqueueStrategy.Wait));

        var pending = queue.EnqueueMany([1, 2, 3], TestToken);

        // The reader holds item 1 on the gate, so the buffer holds at most one
        // more item: the batch must stall on the third until space frees up.
        await WaitUntil(() => processor.Processed == 0 && queue.AvailableCapacity == 0, TestToken);
        Assert.False(pending.IsCompleted, "Wait strategy must wait while the buffer is full");

        processor.Gate.TrySetResult();

        Assert.Equal(3, await pending);

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Skip_Strategy_PartialAcceptance_DropsAndReportsSkipped()
    {
        var processor = new CountingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        Assert.Equal(2, await queue.EnqueueMany([1, 2, 3, 4], TestToken));

        Assert.Equal(2, queue.QueueTasks); // only the accepted items are counted
        Assert.Equal(0, queue.AvailableCapacity);

        Assert.Contains(statuses, s => s.Item == 3 && s.Status == SaWorkStatus.Skipped);
        Assert.Contains(statuses, s => s.Item == 4 && s.Status == SaWorkStatus.Skipped);
        Assert.DoesNotContain(statuses, s => s.Item is 3 or 4 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);

        queue.ConcurrencyLimit = 1; // resume
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed); // only the accepted items were processed
        Assert.Contains(statuses, s => s.Item == 1 && s.Status == SaWorkStatus.Completed);
        Assert.Contains(statuses, s => s.Item == 2 && s.Status == SaWorkStatus.Completed);
        Assert.DoesNotContain(statuses, s => s.Item is 3 or 4 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);
    }

    [Fact]
    public async Task Throw_Strategy_PartialAcceptance_ThrowsWithBatchCounts()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithEnqueueStrategy(SaEnqueueStrategy.Throw));

        var ex = await Record.ExceptionAsync(async () =>
            await queue.EnqueueMany([1, 2, 3, 4], cancellationToken: CancellationToken.None));

        Assert.NotNull(ex);
        Assert.IsType<SaWorkQueueFullException>(ex!);
        var qex = (SaWorkQueueFullException)ex!;
        Assert.Equal(2, qex.QueueCapacity);
        Assert.Equal(2, qex.QueuedCount);
        Assert.Equal(2, qex.AcceptedCount); // items 1 and 2 were accepted
        Assert.Equal(3, qex.TotalCount); // item 3 was attempted and failed; item 4 was never enumerated
        Assert.Null(qex.ItemDisplayName); // batch call: no single-item name
        Assert.Contains("2 of 3", qex.Message);

        // The items accepted before the buffer filled remain in the queue.
        Assert.Equal(2, queue.QueueTasks);

        queue.ConcurrencyLimit = 1; // resume
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Empty_Collection_ReturnsZeroAndReportsNothing()
    {
        var processor = new CountingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        Assert.Equal(0, await queue.EnqueueMany([], TestToken));

        Assert.Equal(0, queue.QueueTasks);
        Assert.Empty(statuses);

        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(0, processor.Processed);
    }

    [Fact]
    public async Task Wait_Strategy_CancelledToken_ThrowsAndDoesNotCount()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(1));

        Assert.True(await queue.Enqueue(1, TestToken)); // fill the only slot: the next write must wait

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Record.ExceptionAsync(async () =>
        {
            await queue.EnqueueMany([2], cts.Token);
        });

        Assert.NotNull(ex);
        Assert.IsAssignableFrom<OperationCanceledException>(ex);

        // The failed item was balanced out; only item 1 remains counted.
        Assert.Equal(1, queue.QueueTasks);

        queue.ConcurrencyLimit = 1; // resume
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(1, processor.Processed);
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Stopped_Queue_Throws()
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2));

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        queue.Shutdown();

        // EnqueueMany is async: the exception is carried by the returned ValueTask.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await queue.EnqueueMany([2], cancellationToken: CancellationToken.None);
        });

        queue.Dispose();
    }

    [Fact]
    public async Task Disposed_Queue_Throws()
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2));

        queue.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await queue.EnqueueMany([1], cancellationToken: CancellationToken.None);
        });
    }
}
