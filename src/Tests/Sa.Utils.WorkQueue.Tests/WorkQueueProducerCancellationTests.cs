using System.Collections;
using System.Collections.Concurrent;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Tests for <see cref="SaProducerCancellationMode"/>: <c>Detached</c> keeps the token
/// for the channel write only, while <c>Observe</c> keeps the pre-existing post-acceptance
/// abort.
/// </summary>
public sealed class WorkQueueProducerCancellationTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private sealed class CompletedProcessor : ISaWork<int>
    {
        public Task Execute(int input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Blocks until the test opens the gate and deliberately ignores the token.</summary>
    private sealed class GateProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;

        public int Completed => Volatile.Read(ref _completed);

        public async Task Execute(int input, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            Interlocked.Increment(ref _completed);
        }
    }

    /// <summary>Runs until the token it is given is cancelled.</summary>
    private sealed class InfiniteProcessor : ISaWork<int>
    {
        public Task Execute(int input, CancellationToken ct)
            => Task.Delay(Timeout.Infinite, ct);
    }

    private static SaWorkQueueOptions<int> Detached(ISaWork<int> processor, int concurrency = 1)
        => SaWorkQueueOptions<int>.Create(processor)
            .WithProducerCancellation(SaProducerCancellationMode.Detached)
            .WithConcurrencyLimit(concurrency);

    [Fact]
    public async Task Detached_InFlightItem_SurvivesProducerCancellation()
    {
        var processor = new GateProcessor();
        var statuses = new ConcurrentBag<SaWorkStatus>();

        using var producerCts = new CancellationTokenSource();
        using var queue = new SaWorkQueue<int>(
            Detached(processor)
                .WithQueueCapacity(4)
                .WithStatusCallback((_, status, _) => statuses.Add(status)));

        await queue.Enqueue(1, producerCts.Token);
        await processor.Entered.Task.WaitAsync(TestToken);

        await producerCts.CancelAsync();
        processor.Gate.TrySetResult();

        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.Equal(1, processor.Completed);
        Assert.Contains(SaWorkStatus.Completed, statuses);
        Assert.DoesNotContain(SaWorkStatus.Aborted, statuses);
    }

    [Fact]
    public async Task Detached_BufferedItem_SurvivesProducerCancellation()
    {
        var processed = new ConcurrentBag<int>();

        using var producerCts = new CancellationTokenSource();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((int input, CancellationToken _) =>
                {
                    processed.Add(input);
                    return Task.CompletedTask;
                })
                .WithProducerCancellation(SaProducerCancellationMode.Detached)
                .WithConcurrencyLimit(0) // paused: the item stays in the buffer
                .WithQueueCapacity(4));

        await queue.Enqueue(1, producerCts.Token);
        await producerCts.CancelAsync();

        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.Contains(1, processed);
    }

    [Fact]
    public async Task Detached_AlreadyCancelledToken_RejectsTheItem()
    {
        using var producerCts = new CancellationTokenSource();
        await producerCts.CancelAsync();

        using var queue = new SaWorkQueue<int>(Detached(new CompletedProcessor()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await queue.Enqueue(1, producerCts.Token));

        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Detached_TokenStillCancelsTheWaitForBufferSpace()
    {
        using var producerCts = new CancellationTokenSource();
        using var queue = new SaWorkQueue<int>(
            Detached(new CompletedProcessor(), concurrency: 0)
                .WithQueueCapacity(1)
                .WithEnqueueStrategy(SaEnqueueStrategy.Wait));

        await queue.Enqueue(1, CancellationToken.None); // fills the only slot

        var waiting = queue.Enqueue(2, producerCts.Token);
        await Task.Delay(50, TestToken); // parked in WriteAsync
        await producerCts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);

        // The rejected item gave its pending count back; the buffered one remains.
        Assert.Equal(1, queue.QueueTasks);
    }

    [Fact]
    public async Task Detached_EnqueueMany_StopsWhenTokenIsCancelledMidBatch()
    {
        using var producerCts = new CancellationTokenSource();
        using var queue = new SaWorkQueue<int>(
            Detached(new CompletedProcessor(), concurrency: 0)
                .WithQueueCapacity(8));

        var inputs = new CancellingSequence(producerCts, cancelBefore: 3);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await queue.EnqueueMany(inputs, producerCts.Token));

        // Items 1 and 2 were accepted; the check before item 3 stopped the batch.
        Assert.Equal(2, queue.QueueTasks);
    }

    [Fact]
    public async Task Detached_QueueShutdown_StillCancelsTheItem()
    {
        var statuses = new ConcurrentBag<SaWorkStatus>();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(new InfiniteProcessor())
                .WithProducerCancellation(SaProducerCancellationMode.Detached)
                .WithConcurrencyLimit(1)
                .WithStatusCallback((_, status, _) => statuses.Add(status)));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken);

        await queue.ShutdownAsync();

        Assert.Contains(SaWorkStatus.Cancelled, statuses);
    }

    [Fact]
    public async Task Observe_IsStillTheDefault_AndAborts()
    {
        var statuses = new ConcurrentBag<SaWorkStatus>();
        using var producerCts = new CancellationTokenSource();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(new InfiniteProcessor())
                .WithConcurrencyLimit(1)
                .WithStatusCallback((_, status, _) => statuses.Add(status)));

        await queue.Enqueue(1, producerCts.Token);
        await Task.Delay(50, TestToken);

        await producerCts.CancelAsync();
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.Contains(SaWorkStatus.Aborted, statuses);
    }

    [Fact]
    public void Validator_UndefinedProducerCancellation_Fails()
    {
        var validator = new SaWorkQueueSettingsValidator();

        var result = validator.Validate(null, new SaWorkQueueSettings
        {
            ProducerCancellation = (SaProducerCancellationMode)42
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("ProducerCancellation"));
    }

    private sealed class CancellingSequence(CancellationTokenSource cts, int cancelBefore) : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator()
        {
            for (var i = 1; i <= 5; i++)
            {
                if (i == cancelBefore)
                {
                    cts.Cancel();
                }

                yield return i;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
