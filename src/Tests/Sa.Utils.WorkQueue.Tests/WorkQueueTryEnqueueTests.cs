using System.Collections.Concurrent;

namespace Sa.Utils.WorkQueue.Tests;

public sealed class WorkQueueTryEnqueueTests
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

    [Fact]
    public async Task TryEnqueue_AcceptsWhenBufferHasSpace()
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2));

        Assert.True(queue.TryEnqueue(1));
        Assert.True(queue.TryEnqueue(2));

        Assert.Equal(2, queue.QueueTasks);
        Assert.Equal(0, queue.AvailableCapacity);

        queue.ConcurrencyLimit = 1; // resume: readers drain the buffer
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed);

        Assert.True(queue.TryEnqueue(3)); // buffer free again
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(3, processor.Processed);
    }

    [Fact]
    public async Task TryEnqueue_ReturnsFalseWhenFull_DoesNotCountButReportsSkipped()
    {
        var processor = new CountingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(2)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        Assert.True(queue.TryEnqueue(1));
        Assert.True(queue.TryEnqueue(2));
        Assert.False(queue.TryEnqueue(3)); // buffer full: dropped

        Assert.Equal(2, queue.QueueTasks); // dropped item is not counted
        Assert.Equal(0, queue.AvailableCapacity);
        Assert.Contains(statuses, s => s.Item == 3 && s.Status == SaWorkStatus.Skipped); // dropped item is reported
        Assert.DoesNotContain(statuses, s => s.Item == 3 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);

        queue.ConcurrencyLimit = 1; // resume
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(2, processor.Processed); // dropped item was never processed
        Assert.DoesNotContain(statuses, s => s.Item == 3 && s.Status is SaWorkStatus.Running or SaWorkStatus.Completed);
    }

    [Theory]
    [InlineData(SaEnqueueStrategy.Wait)]
    [InlineData(SaEnqueueStrategy.Skip)]
    [InlineData(SaEnqueueStrategy.Throw)]
    public void TryEnqueue_NeverBlocksOrThrowsWhenFull_RegardlessOfStrategy(SaEnqueueStrategy strategy)
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithQueueCapacity(1)
                .WithEnqueueStrategy(strategy));

        Assert.True(queue.TryEnqueue(1));  // buffer: 1 = full
        Assert.False(queue.TryEnqueue(2)); // full: no block (Wait), no throw (Throw)

        queue.ConcurrencyLimit = 1;
        Assert.True(queue.TryEnqueue(3)); // buffer free after the first drain
    }

    [Fact]
    public async Task TryEnqueue_ThrowsWhenStopped()
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2));

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        queue.Shutdown();

        // TryEnqueue is a plain method: it throws synchronously.
        Assert.Throws<InvalidOperationException>(() => queue.TryEnqueue(2));
        queue.Dispose();
    }

    [Fact]
    public void TryEnqueue_ThrowsWhenDisposed()
    {
        var processor = new CountingProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2));

        queue.Dispose();

        Assert.Throws<ObjectDisposedException>(() => queue.TryEnqueue(1));
    }
}
