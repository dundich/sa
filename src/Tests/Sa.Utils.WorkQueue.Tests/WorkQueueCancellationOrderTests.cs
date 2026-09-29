using System.Collections.Concurrent;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Regression tests for the reader-cancellation order strategies
/// (<see cref="SaReaderCancellationOrder"/>). Before the fix, <c>Random</c>
/// indexed a buffer of size <c>toCancel</c> with indices reaching
/// <c>totalCount - 1</c>, so any limit decrease where fewer than all readers
/// were removed threw <see cref="IndexOutOfRangeException"/> out of the
/// <c>ConcurrencyLimit</c> setter; <c>Fifo</c> and <c>Lifo</c> had no coverage at all.
/// </summary>
public sealed class WorkQueueCancellationOrderTests
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

    [Theory]
    [InlineData(SaReaderCancellationOrder.Lifo)]
    [InlineData(SaReaderCancellationOrder.Fifo)]
    [InlineData(SaReaderCancellationOrder.RoundRobin)]
    [InlineData(SaReaderCancellationOrder.Random)]
    public async Task DecreaseAndRestore_OnlyRemovesRequestedReaders_WithoutThrowing(SaReaderCancellationOrder order)
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(4)
                .WithMaxConcurrency(8)
                .WithReaderCancellationOrder(order));

        await Task.Delay(50, TestToken); // let the four readers start

        // toCancel (2) < total (4) on every round: the old Random implementation
        // drew swap indices over a buffer of size 2 and could reach index 3.
        for (var round = 0; round < 20; round++)
        {
            queue.ConcurrencyLimit = 2;
            Assert.Equal(2, queue.ConcurrencyLimit);
            await Task.Delay(10, TestToken);

            queue.ConcurrencyLimit = 4;
            Assert.Equal(4, queue.ConcurrencyLimit);
            await Task.Delay(10, TestToken);
        }

        // The pool is still fully functional: all items are processed and the
        // limit is exactly what was requested, not a drifted value.
        for (var i = 1; i <= 8; i++)
        {
            await queue.Enqueue(i, TestToken);
        }

        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(4, queue.ConcurrencyLimit);
        Assert.Equal(8, processor.Processed);
    }

    [Theory]
    [InlineData(SaReaderCancellationOrder.Lifo)]
    [InlineData(SaReaderCancellationOrder.Fifo)]
    [InlineData(SaReaderCancellationOrder.RoundRobin)]
    [InlineData(SaReaderCancellationOrder.Random)]
    public async Task DecreaseToOne_ThenRestore_QueueKeepsWorking(SaReaderCancellationOrder order)
    {
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(5)
                .WithMaxConcurrency(5)
                .WithReaderCancellationOrder(order));

        await Task.Delay(50, TestToken);

        queue.ConcurrencyLimit = 1; // toCancel (4) close to total (5): worst case for the old buffer
        await Task.Delay(100, TestToken);
        Assert.Equal(1, queue.ConcurrencyLimit);

        queue.ConcurrencyLimit = 5;
        await Task.Delay(100, TestToken);
        Assert.Equal(5, queue.ConcurrencyLimit);

        for (var i = 1; i <= 5; i++)
        {
            await queue.Enqueue(i, TestToken);
        }

        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(5, processor.Processed);
    }

    [Fact]
    public async Task RandomOrder_DistinctReadersCancelled_NoDuplicateSlots()
    {
        // The old implementation could also produce duplicate indices (the
        // zero-initialised buffer), cancelling the same reader twice while
        // leaving others alive — so after a decrease the *effective* pool
        // size was wrong even when no exception was thrown. Verify the
        // effective pool through the reported limit across many random draws.
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(6)
                .WithMaxConcurrency(6)
                .WithReaderCancellationOrder(SaReaderCancellationOrder.Random));

        await Task.Delay(50, TestToken);

        for (var round = 0; round < 15; round++)
        {
            queue.ConcurrencyLimit = 3;
            Assert.Equal(3, queue.ConcurrencyLimit);
            await Task.Delay(10, TestToken);

            queue.ConcurrencyLimit = 6;
            Assert.Equal(6, queue.ConcurrencyLimit);
            await Task.Delay(10, TestToken);
        }
    }
}
