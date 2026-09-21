namespace Sa.Utils.WorkQueue.Tests;

public sealed class WorkQueueStabilityTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class CountingProcessor : ISaWork<int>
    {
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public Task Execute(int input, CancellationToken ct)
        {
            if (input == -1) throw new InvalidOperationException("Simulated fault");
            Interlocked.Increment(ref _processed);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public async Task Execute(int input, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            if (input == -1) throw new InvalidOperationException("Simulated fault");
            Interlocked.Increment(ref _processed);
        }
    }

    private sealed class NonCooperativeProcessor : ISaWork<int>
    {
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public async Task Execute(int input, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), TestToken);
            Interlocked.Increment(ref _processed);
        }
    }

    [Fact]
    public async Task WaitForIdle_DuringShutdown_CompletesNormally()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor).WithConcurrencyLimit(2));

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        var shutdownTask = queue.ShutdownAsync();
        await Task.Delay(10, TestToken);

        await queue.WaitForIdleAsync(TestToken);

        await shutdownTask;
        Assert.False(queue.IsEnabled);
    }

    [Fact]
    public async Task StopReader_ReducesConcurrency_NoAutoReplace()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithHandleItemFaulted((_, _) => SaExecutionErrorStrategy.StopReader));

        await queue.Enqueue(-1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(1, queue.ConcurrencyLimit);

        await Task.Delay(200, TestToken);

        Assert.Equal(1, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task RapidLimitChanges_NoSpuriousReaders()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithMaxConcurrency(10));

        for (int i = 0; i < 20; i++)
        {
            queue.ConcurrencyLimit = (i % 5) + 1;
            await Task.Delay(5, TestToken);
        }

        queue.ConcurrencyLimit = 3;
        await Task.Delay(100, TestToken);

        Assert.Equal(3, queue.ConcurrencyLimit);

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(1, processor.Processed);
    }

    [Fact]
    public async Task DecreaseThenIncrease_NoReaderLeak()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(4)
                .WithMaxConcurrency(8));

        await Task.Delay(50, TestToken);
        queue.ConcurrencyLimit = 1;
        await Task.Delay(100, TestToken);

        queue.ConcurrencyLimit = 4;
        await Task.Delay(100, TestToken);

        Assert.Equal(4, queue.ConcurrencyLimit);

        var items = Enumerable.Range(1, 8).ToArray();
        foreach (var item in items)
            await queue.Enqueue(item, TestToken);

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(8, processor.Processed);
    }

    [Fact]
    public async Task StatusCallbackException_DoesNotBlockOtherReaders()
    {
        var processor = new CountingProcessor();
        var callbackFailures = 0;

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithStatusCallback((item, status, ex) =>
                {
                    if (item == 99 && status == SaWorkStatus.Running)
                    {
                        Interlocked.Increment(ref callbackFailures);
                        throw new InvalidOperationException("Callback fault");
                    }
                }));

        await queue.Enqueue(1, TestToken);
        await queue.Enqueue(99, TestToken);
        await queue.Enqueue(2, TestToken);

        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(3, processor.Processed);
        Assert.True(callbackFailures > 0);
    }

    [Fact]
    public async Task SyncShutdown_WithStuckReader_CompletesViaTimeout()
    {
        var processor = new NonCooperativeProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(1)
                .WithShutdownTimeout(TimeSpan.FromSeconds(1)));

        await queue.Enqueue(1, TestToken);

#pragma warning disable xUnit1051
        var shutdownTask = Task.Run(() => queue.Shutdown());
#pragma warning restore xUnit1051
        var completed = await Task.WhenAny(shutdownTask, Task.Delay(TimeSpan.FromSeconds(5), TestToken));
        Assert.True(completed == shutdownTask, "Sync shutdown should complete within timeout");
        queue.Dispose();
    }

    [Fact]
    public async Task ForceCancelReaders_ConcurrencyLimitReflectsZero()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(3)
                .WithMaxConcurrency(5));

        await queue.ForceCancelReadersAsync(ct: TestToken);
        await Task.Delay(50, TestToken);

        Assert.Equal(0, queue.ConcurrencyLimit);

        queue.ConcurrencyLimit = 3;
        await Task.Delay(50, TestToken);
        Assert.Equal(3, queue.ConcurrencyLimit);

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(1, processor.Processed);
    }

    [Fact]
    public async Task MultipleStopReaders_CorrectCapacityDecrease()
    {
        var processor = new CountingProcessor();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithMaxConcurrency(4)
                .WithHandleItemFaulted((item, _) =>
                    item == -1 ? SaExecutionErrorStrategy.StopReader : SaExecutionErrorStrategy.Continue));

        await queue.Enqueue(-1, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        await Task.Delay(100, TestToken);
        Assert.Equal(0, queue.ConcurrencyLimit);
    }

    [Fact]
    public void MaxConcurrency_ReturnsConfiguredValue()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithMaxConcurrency(7)
                .WithConcurrencyLimit(2));

        Assert.Equal(7, queue.MaxConcurrency);
    }

    [Fact]
    public void QueueCapacity_ReturnsConfiguredValue()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithQueueCapacity(42)
                .WithConcurrencyLimit(2));

        Assert.Equal(42, queue.QueueCapacity);
    }

    [Fact]
    public async Task ForceCancelReaders_Sync_CancelsAllReaders()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(3)
                .WithMaxConcurrency(5)
                .WithShutdownTimeout(TimeSpan.FromSeconds(5)));

        queue.ForceCancelReaders();
        Assert.Equal(0, queue.ConcurrencyLimit);

        queue.ConcurrencyLimit = 3;
        await Task.Delay(50, TestToken);
        Assert.Equal(3, queue.ConcurrencyLimit);

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(1, processor.Processed);
    }

    [Fact]
    public async Task WaitForIdle_WhilePaused_SkipsWait()
    {
        var processor = new BlockingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1));

        await queue.Enqueue(1, TestToken);
        await queue.Enqueue(2, TestToken);

        // Let the single reader pick up item 1 (it blocks on the gate); item 2 stays queued.
        await Task.Delay(50, TestToken);

        // Pause: the in-flight item is cancelled and the reader is removed,
        // leaving item 2 queued with no readers to process it.
        queue.ConcurrencyLimit = 0;
        await Task.Delay(50, TestToken);

        // With concurrency 0 and pending items, WaitForIdleAsync must not block
        // for an idle state no reader can reach — it skips the wait and returns.
        var wait = queue.WaitForIdleAsync(TestToken);
        var completed = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(completed, wait),
            "WaitForIdleAsync did not return within 500ms while paused; expected an immediate skip.");
        await wait;
    }

    [Fact]
    public async Task Enqueue_AfterPause_IsAcceptedButWaitsUntilLimitRestored()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1));

        // Bring the queue to a steady state first.
        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(1, processor.Processed);

        // Pause: the only reader is cancelled, but the queue stays Active
        // (pause is not a shutdown).
        queue.ConcurrencyLimit = 0;
        await Task.Delay(50, TestToken);
        Assert.True(queue.IsEnabled, "Pause must not disable the queue");

        // Enqueue while paused: accepted (state Active, channel open),
        // but no reader exists, so the item just sits in the channel.
        await queue.Enqueue(2, TestToken);

        Assert.Equal(1, queue.QueueTasks);
        Assert.False(queue.IsIdle());

        await Task.Delay(200, TestToken);
        Assert.Equal(1, processor.Processed);

        // WaitForIdleAsync must not block for an idle state no reader can reach.
        var wait = queue.WaitForIdleAsync(TestToken);
        var done = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(done, wait),
            "WaitForIdleAsync did not return within 500ms while paused; expected an immediate skip.");
        await wait;

        // Restore the limit: a reader is spawned and the pending item is processed.
        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);
    }
}
