using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// What <see cref="ISaWorkQueue{TInput}.PoolState"/> reports, and the one case it
/// was added for.
/// </summary>
/// <remarks>
/// The audit behind it (P5) said the pool was unobservable, and proposed a live reader
/// count. The count turned out to be the wrong instrument: a force-cancel releases the
/// slots of the readers it stops, so both the count and the limit read <c>0</c> after
/// one, and both read <c>0</c> after a deliberate pause. The first test here is the one
/// that makes that concrete — two queues, identical published numbers, different states.
/// </remarks>
public sealed class WorkQueuePoolStateTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void PoolState_OnAFreshQueue_IsActive()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(2));

        Assert.True(queue.IsEnabled);
        Assert.Equal(SaWorkPoolState.Active, queue.PoolState);
    }

    [Fact]
    public async Task PoolState_AfterPausing_IsPaused_AndRaisingTheLimitRestoresIt()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(2)
                .WithQueueCapacity(8));

        queue.ConcurrencyLimit = 0;

        // A pause is deliberate, and the queue says so rather than looking like a pool
        // that died. It is still enabled: items are being accepted, just not taken.
        Assert.True(queue.IsEnabled);
        Assert.Equal(SaWorkPoolState.Paused, queue.PoolState);

        queue.ConcurrencyLimit = 2;
        Assert.Equal(SaWorkPoolState.Active, queue.PoolState);

        // The pool really is back, not just relabelled: the item queued while paused is
        // picked up now, which is the difference between a pause and a dead pool.
        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.True(queue.IsIdle());
    }

    /// <summary>
    /// The finding itself: a force-cancelled pool and a paused one published exactly the
    /// same numbers, and only a third value tells them apart.
    /// </summary>
    [Fact]
    public async Task PoolState_AfterForceCancel_IsNoReaders_WhichThePublishedNumbersNeverSaid()
    {
        var processor = new GateProcessor();

        using var cancelled = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8));

        using var paused = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8));

        // Two queues, two ways of ending up with no readers.
        await cancelled.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);
        await cancelled.ForceCancelReadersAsync(ct: TestToken);

        paused.ConcurrencyLimit = 0;

        // Identical as far as a caller could previously see: enabled, limit zero, no
        // reader. This is why a count was not the answer — a count would have read zero
        // here as well.
        Assert.Equal(paused.IsEnabled, cancelled.IsEnabled);
        Assert.True(cancelled.IsEnabled, "a force-cancel leaves the queue enabled");
        Assert.Equal(0, cancelled.ConcurrencyLimit);
        Assert.Equal(0, paused.ConcurrencyLimit);

        // And different here, which is the entire point of the property.
        Assert.Equal(SaWorkPoolState.NoReaders, cancelled.PoolState);
        Assert.Equal(SaWorkPoolState.Paused, paused.PoolState);
    }

    [Fact]
    public async Task PoolState_AfterEveryReaderFaulted_IsNoReaders()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => throw new InvalidOperationException("boom"))
                .WithConcurrencyLimit(1)
                .WithHandleItemFaulted((_, _) => SaExecutionErrorStrategy.StopReader));

        await queue.Enqueue(1, TestToken);

        // The reader is stopped from its own thread, so the pool drains a moment after the
        // item is done with; poll rather than assume the order.
        await WaitUntilAsync(() => queue.PoolState == SaWorkPoolState.NoReaders, TestToken);

        Assert.True(queue.IsEnabled, "a reader fault is not a queue shutdown");
    }

    [Fact]
    public async Task PoolState_AfterShutdown_IsStopped()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1));

        await queue.ShutdownAsync();

        Assert.False(queue.IsEnabled);
        Assert.Equal(SaWorkPoolState.Stopped, queue.PoolState);
    }

    [Fact]
    public async Task PoolState_AfterDispose_IsStopped()
    {
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1));

        // No using: the queue is disposed here, and disposing it again would be the only
        // thing left for the compiler-generated call to do.
        await queue.DisposeAsync();

        Assert.Equal(SaWorkPoolState.Stopped, queue.PoolState);
    }

    /// <summary>
    /// A queue that was paused and then stopped has no pool left to describe, and the
    /// state has to say so.
    /// </summary>
    /// <remarks>
    /// This pins the order of the checks in the property. Reading the pause first would
    /// report <see cref="SaWorkPoolState.Paused"/> for a queue that is not paused, it is
    /// gone — the same kind of claim that is true in a way that does not help, and the
    /// reason the state is not derivable from <c>ConcurrencyLimit == 0</c>.
    /// </remarks>
    [Fact]
    public async Task PoolState_PausedThenStopped_IsStopped_AndNotPaused()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1));

        queue.ConcurrencyLimit = 0;
        Assert.Equal(SaWorkPoolState.Paused, queue.PoolState);

        await queue.ShutdownAsync();

        // The limit is still zero, which is exactly why it could not answer this.
        Assert.Equal(0, queue.ConcurrencyLimit);
        Assert.Equal(SaWorkPoolState.Stopped, queue.PoolState);
    }

    /// <summary>
    /// <see cref="SaWorkPoolState.NoReaders"/> is a report, not a verdict: the documented
    /// response is to set the limit, and that has to bring the pool back.
    /// </summary>
    [Fact]
    public async Task PoolState_AfterReArmingAnEmptyPool_IsActive_AndTheItemIsTaken()
    {
        var processor = new GateProcessor();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithMaxConcurrency(4));

        await queue.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);
        await queue.ForceCancelReadersAsync(ct: TestToken);

        Assert.Equal(SaWorkPoolState.NoReaders, queue.PoolState);

        // An item that arrives while the pool is empty: only the re-armed pool can take
        // it, which is what makes this a test of recovery rather than of the state
        // change. The gate is opened first — the first item was interrupted by the
        // force-cancel, so the second is the only one that can ever finish.
        var second = queue.Enqueue(2, TestToken);
        processor.Gate.TrySetResult();

        queue.ConcurrencyLimit = 2;

        Assert.Equal(SaWorkPoolState.Active, queue.PoolState);

        await second;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.True(queue.IsIdle());
        Assert.Contains(2, processor.CompletedItems);
    }

    /// <summary>
    /// The state and the warning <c>WaitForIdleAsync</c> logs name the same thing.
    /// </summary>
    /// <remarks>
    /// The warning <c>WaitForIdleAsync</c> logs is now derived from <c>PoolState</c>
    /// rather than decided beside it, so the two cannot name different causes by
    /// construction. This test is what a future reader would run to confirm that is
    /// still true: it asserts the end-to-end consequence — a caller polling the state
    /// and a wait that gave up hear the same word — which a future change to either
    /// side alone would break.
    /// </remarks>
    [Fact]
    public async Task PoolState_NamesTheSameReasonThatWaitForIdleAsyncLogs()
    {
        var logger = new RecordingLogger<SaWorkQueue<int>>();

        using var paused = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8),
            logger);

        // Both waits need something pending that nothing will take; otherwise they return
        // at the top having logged nothing and there is no reason to compare.
        //
        // The paused queue is paused *before* the item is enqueued, so no reader can
        // have taken it and finished it in between. The emptied one gets its item from a
        // dead pool instead — a force-cancel that waited for its readers would have
        // drained the buffer on the way out, leaving nothing to be honest about.
        paused.ConcurrencyLimit = 0;
        await paused.Enqueue(1, TestToken);

        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var emptied = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => throw new InvalidOperationException("boom"))
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithHandleItemFaulted((_, _) =>
                {
                    faulted.TrySetResult();
                    return SaExecutionErrorStrategy.StopReader;
                }),
            logger);

        await emptied.Enqueue(1, TestToken);
        await faulted.Task.WaitAsync(TestToken);
        await WaitUntilAsync(() => emptied.PoolState == SaWorkPoolState.NoReaders, TestToken);

        // Nothing is left to take this one: that is the state being reported.
        await emptied.Enqueue(2, TestToken);

        await paused.WaitForIdleAsync(cancellationToken: TestToken);
        await emptied.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.Equal(SaWorkPoolState.Paused, paused.PoolState);
        Assert.True(logger.Contains(LogLevel.Warning, "Paused"),
            "a wait that gave up on a paused queue must say so");

        Assert.Equal(SaWorkPoolState.NoReaders, emptied.PoolState);
        Assert.True(logger.Contains(LogLevel.Warning, "NoReaders"),
            "a wait that gave up on an empty pool must say so");
    }

    /// <summary>Polls a condition the code under test reaches on its own thread.</summary>
    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    /// <summary>A processor that blocks until its gate opens or its token fires.</summary>
    private sealed class GateProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource EnteredSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Inputs that ran to completion, so a test can name one.</summary>
        public readonly ConcurrentBag<int> CompletedItems = [];

        public Task Entered => EnteredSource.Task;

        public async Task Execute(int input, CancellationToken ct)
        {
            EnteredSource.TrySetResult();

            try
            {
                await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
                CompletedItems.Add(input);
            }
            catch (OperationCanceledException)
            {
                // The shape of an item interrupted by a cancel; not a failure.
            }
        }
    }
}
