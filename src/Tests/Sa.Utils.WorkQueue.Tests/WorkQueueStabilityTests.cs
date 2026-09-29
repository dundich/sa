using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Takes one item and refuses to give it up, ignoring its cancellation token —
    /// so a shutdown gives up waiting for it and drains around it.
    /// </summary>
    private sealed class StubbornProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource EnteredSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => EnteredSource.Task;

        public void Release() => Gate.TrySetResult();

        public async Task Execute(int input, CancellationToken ct)
        {
            EnteredSource.TrySetResult();

            // No ct: the whole point is a reader that outlives the shutdown wait.
            await Gate.Task;
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

    private sealed class GateIgnoringProcessor : ISaWork<int>
    {
        // Unlike GateProcessor, this one never observes ct: it models a
        // processor that stays stuck in its work long after its reader was
        // cancelled, so the reader's finally block runs late.
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public async Task Execute(int input, CancellationToken ct)
        {
            await Gate.Task;
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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        var shutdownTask = queue.ShutdownAsync();
        await Task.Delay(10, TestToken);

        await queue.WaitForIdleAsync(cancellationToken: TestToken);

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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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

        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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

        await queue.WaitForIdleAsync(cancellationToken: TestToken);

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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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
        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
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
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
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
        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var done = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(done, wait),
            "WaitForIdleAsync did not return within 500ms while paused; expected an immediate skip.");
        await wait;

        // Restore the limit: a reader is spawned and the pending item is processed.
        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(2, processor.Processed);
    }

    [Fact]
    public async Task Pause_SoftMode_InFlightItemCompletesAndQueuedItemSurvives()
    {
        var processor = new BlockingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithReaderCancelMode(SaReaderCancelMode.Soft)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken); // Reader picks up item 1 and blocks on the gate
        await queue.Enqueue(2, TestToken); // Item 2 stays in the channel

        queue.ConcurrencyLimit = 0; // Pause: soft mode must not cancel the in-flight item
        await Task.Delay(50, TestToken);

        processor.Gate.SetResult(); // Release the in-flight item
        await Task.Delay(50, TestToken);

        Assert.Equal(1, processor.Processed); // The in-flight item completed
        Assert.Equal(1, queue.QueueTasks); // Item 2 is still in the queue

        Assert.DoesNotContain(statuses,
            s => s.Item == 2 && s.Status is SaWorkStatus.Cancelled or SaWorkStatus.Faulted);

        queue.ConcurrencyLimit = 1; // Resume: a new reader picks up item 2
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(2, processor.Processed);
        Assert.Contains(statuses, s => s.Item == 2 && s.Status == SaWorkStatus.Completed);
    }

    [Fact]
    public async Task DecreaseLimit_SoftMode_InFlightItemsComplete()
    {
        var processor = new BlockingProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithReaderCancelMode(SaReaderCancelMode.Soft)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        await queue.Enqueue(1, TestToken);
        await queue.Enqueue(2, TestToken);
        await Task.Delay(50, TestToken); // Both readers block on the shared gate
        await queue.Enqueue(3, TestToken); // Item 3 stays in the channel

        queue.ConcurrencyLimit = 1; // One reader is removed softly — its in-flight item survives
        await Task.Delay(50, TestToken);

        processor.Gate.SetResult(); // Both in-flight items complete
        await Task.Delay(50, TestToken);

        // The removed reader's in-flight item completed (not cancelled). The
        // surviving reader may have already picked up item 3 from the channel,
        // so only assert on statuses, not on the exact processed count.
        Assert.Contains(statuses, s => s.Item == 1 && s.Status == SaWorkStatus.Completed);
        Assert.Contains(statuses, s => s.Item == 2 && s.Status == SaWorkStatus.Completed);
        Assert.DoesNotContain(statuses, s => s.Status == SaWorkStatus.Cancelled);

        queue.ConcurrencyLimit = 2; // Restore capacity
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(3, processor.Processed);
        Assert.Contains(statuses, s => s.Item == 3 && s.Status == SaWorkStatus.Completed);
    }

    [Fact]
    public async Task Shutdown_SoftMode_StillCancelsInFlightWork()
    {
        var processor = new BlockingProcessor();
        var cancelled = 0;

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithReaderCancelMode(SaReaderCancelMode.Soft)
                .WithStatusCallback((_, status, _) =>
                {
                    if (status == SaWorkStatus.Cancelled)
                        Interlocked.Increment(ref cancelled);
                }));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken); // Reader blocks on the gate

        await queue.ShutdownAsync();

        Assert.False(queue.IsEnabled);
        Assert.Equal(1, cancelled); // Soft mode must not protect against shutdown
        Assert.Equal(0, processor.Processed);
    }

    [Fact]
    public async Task ForceCancel_SoftMode_RemainsHard()
    {
        var processor = new BlockingProcessor();
        var cancelled = 0;

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithReaderCancelMode(SaReaderCancelMode.Soft)
                .WithStatusCallback((_, status, _) =>
                {
                    if (status == SaWorkStatus.Cancelled)
                        Interlocked.Increment(ref cancelled);
                }));

        await queue.Enqueue(1, TestToken);
        await queue.Enqueue(2, TestToken);
        await Task.Delay(50, TestToken); // Both readers block on the shared gate

        await queue.ForceCancelReadersAsync(ct: TestToken);

        Assert.Equal(0, queue.ConcurrencyLimit);
        Assert.Equal(2, cancelled); // Force cancel stays hard even in soft mode
        Assert.Equal(0, processor.Processed);
    }

    [Fact]
    public async Task ForceCancelReaders_RacingLimitReArm_DyingReadersDoNotEatNewLimit()
    {
        // The JobScheduler pattern: an emergency stop is fired from a background
        // task while the main path re-arms the limit. The in-flight work ignores
        // cancellation, so the cancelled readers are still alive (their finally
        // has not run) at re-arm time — and it was exactly that late finally
        // that used to decrement the freshly assigned limit to 0.
        var processor = new GateIgnoringProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(4)
                .WithMaxConcurrency(4)
                .WithShutdownTimeout(TimeSpan.FromSeconds(5)));

        for (var i = 1; i <= 4; i++)
        {
            await queue.Enqueue(i, TestToken);
        }
        await Task.Delay(100, TestToken); // all four readers are stuck in the work

        var cancel = Task.Run(async () =>
        {
            try
            {
                await queue.ForceCancelReadersAsync(timeout: TimeSpan.FromMilliseconds(300), ct: TestToken);
            }
            catch (TimeoutException)
            {
                // Expected: the stuck readers do not finish within the bounded wait.
            }
        }, TestToken);
        await Task.Delay(150, TestToken); // the force-cancel accounting has landed

        // Re-arm while the cancelled readers are still dying.
        queue.ConcurrencyLimit = 4;
        await Task.Delay(200, TestToken); // let the dying readers reach their finally

        processor.Gate.TrySetResult(); // release the stuck work
        await cancel;

        // The four dying readers must not eat the limit the caller just set.
        Assert.Equal(4, queue.ConcurrencyLimit);

        // And the pool must be usable again: the restored readers process new work.
        await queue.Enqueue(5, TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.True(queue.IsIdle());
        // The four stuck items completed once the gate opened (the processor
        // ignores cancellation), plus the new item 5.
        Assert.Equal(5, processor.Processed);
    }

    [Fact]
    public async Task WaitForIdle_FailIfPaused_ThrowsWhenExplicitlyPausedWithPendingWork()
    {
        var processor = new BlockingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor).WithConcurrencyLimit(1));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken); // reader picks up item 1 and blocks on the gate
        await queue.Enqueue(2, TestToken); // item 2 stays in the channel

        queue.ConcurrencyLimit = 0; // explicit pause
        await Task.Delay(50, TestToken);

        // failIfNoProgress turns the silent no-progress into an error for callers
        // that must not proceed without progress.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken));

        // The default keeps the old behavior: return immediately, no throw.
        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var completed = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(completed, wait),
            "WaitForIdleAsync must return immediately while paused");
        await wait;

        // Resume: a real wait happens again and must not throw.
        processor.Gate.TrySetResult(); // item 2's replacement reader must not block
        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.True(queue.IsIdle());
        // Only item 2 completed; item 1 was cancelled by the pause.
        Assert.Equal(1, processor.Processed);
    }

    [Fact]
    public async Task WaitForIdle_FailIfPaused_IdleQueue_ReturnsImmediately()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(new CountingProcessor()).WithConcurrencyLimit(1));

        // No work at all: the wait returns immediately regardless of the flag.
        await queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken);

        // Paused but still idle: there is no pending work to complain about.
        queue.ConcurrencyLimit = 0;
        await queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken);

        Assert.True(queue.IsIdle());
    }

    [Fact]
    public async Task WaitForIdle_NoReaders_AfterForceCancel_ReturnsInsteadOfHanging()
    {
        // The pool can also become empty without an explicit pause: every reader
        // force-cancelled (or lost to a fault) and nobody re-arms it. The queue
        // is Active and accepts items, but no reader can ever drain them — the
        // wait must report the state instead of blocking forever.
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(2)
                .WithMaxConcurrency(4));

        await queue.ForceCancelReadersAsync(ct: TestToken);
        await queue.Enqueue(1, TestToken); // accepted, but no readers can drain it

        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var completed = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(completed, wait),
            "WaitForIdleAsync must not block while the queue has no live readers");
        await wait;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken));

        // Re-arming the limit restores the real wait and the pool.
        queue.ConcurrencyLimit = 2;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.True(queue.IsIdle());
        Assert.Equal(1, processor.Processed);
    }

    /// <summary>
    /// A queue that was paused and then stopped still has a drain to wait for, and the
    /// wait must be that wait.
    /// </summary>
    /// <remarks>
    /// The old code checked <c>_paused</c> before anything else, so this queue was
    /// reported <c>Paused</c> — "no reader can process these" — while a shutdown was
    /// actively draining the buffer. It returned while work was still pending, and
    /// <c>IsIdle()</c> said <c>false</c> a moment later. With <c>failIfNoProgress</c>
    /// it was worse than useless: it threw <c>QueuePaused</c>, whose message tells the
    /// caller to raise <c>ConcurrencyLimit</c> on a queue where that assignment does
    /// nothing.
    /// <para>
    /// The setup has to keep one item in flight past the shutdown for the wait to have
    /// something to park on: a processor that ignores its cancellation token, so the
    /// drain runs, drops the buffered item, and finds a reader still holding the other
    /// one. That reader is what makes <c>IsIdle()</c> false afterwards, and the wait
    /// cannot return until it lets go.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WaitForIdle_OnAPausedThenStoppedQueue_WaitsForTheDrain()
    {
        var processor = new StubbornProcessor();
        var logger = new RecordingLogger<SaWorkQueue<int>>();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithShutdownTimeout(TimeSpan.FromMilliseconds(200)),
            logger);

        await queue.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);
        await queue.Enqueue(2, TestToken); // stays in the buffer, the reader is busy

        queue.ConcurrencyLimit = 0;      // paused
        await queue.ShutdownAsync();     // ... and then stopped

        // The drain has run: item 2 is gone, item 1 is still held by the reader that
        // ignored its cancellation. Work is genuinely pending, so this wait has to wait.
        Assert.False(queue.IsIdle());
        Assert.Equal(SaWorkPoolState.Stopped, queue.PoolState);
        // The limit is still zero, which is exactly why it could not be the reason.
        Assert.Equal(0, queue.ConcurrencyLimit);

        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var raced = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.False(ReferenceEquals(raced, wait),
            "a stopped queue is draining; the wait must not report the old pause instead");

        processor.Release();
        await wait.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        Assert.True(queue.IsIdle());
        Assert.False(logger.Contains(LogLevel.Warning, "WaitForIdleAsync returned with"),
            "a wait that actually waited has nothing to confess");
    }

    /// <summary>
    /// The same queue with <c>failIfNoProgress</c>, where the old answer was an
    /// exception carrying advice that cannot work.
    /// </summary>
    [Fact]
    public async Task WaitForIdle_FailIfNoProgress_OnAPausedThenStoppedQueue_DoesNotThrowQueuePaused()
    {
        var processor = new StubbornProcessor();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithShutdownTimeout(TimeSpan.FromMilliseconds(200)));

        await queue.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);
        await queue.Enqueue(2, TestToken);

        queue.ConcurrencyLimit = 0;
        await queue.ShutdownAsync();

        Assert.False(queue.IsIdle());

        // QueuePaused says "raise ConcurrencyLimit above 0". On a stopped queue that
        // assignment is a no-op — L2 removed the write — so the exception was telling
        // the caller to do something that cannot help.
        var wait = queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken);
        var raced = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.False(ReferenceEquals(raced, wait),
            "failIfNoProgress must not turn a queue that is draining into an error");

        processor.Release();
        await wait.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        Assert.True(queue.IsIdle());
    }

    /// <summary>
    /// The branches the unification must not disturb: a queue that is still running
    /// keeps both early returns, and each one still names its own cause.
    /// </summary>
    [Fact]
    public async Task WaitForIdle_OnALiveQueue_KeepsBothEarlyReturnsAndTheirCauses()
    {
        var logger = new RecordingLogger<SaWorkQueue<int>>();

        // Paused while still enabled: returns, logs Paused, throws QueuePaused.
        using (var paused = new SaWorkQueue<int>(
                   SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                       .WithConcurrencyLimit(1)
                       .WithQueueCapacity(8),
                   logger))
        {
            paused.ConcurrencyLimit = 0;
            await paused.Enqueue(1, TestToken);

            await paused.WaitForIdleAsync(cancellationToken: TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

            // On the message, not just the type: both causes throw
            // InvalidOperationException, and the text is the only thing that tells the
            // caller which of the two it hit and what to do about it.
            var pausedThrow = await Assert.ThrowsAsync<InvalidOperationException>(
                () => paused.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken));
            Assert.Contains("paused", pausedThrow.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Empty pool while still enabled: returns, logs NoReaders, throws
        // QueueHasNoReaders. The two are different causes and the caller can only act
        // on one of them, so a single "no progress" would have been a loss.
        var processor = new CountingProcessor();
        using var emptied = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithMaxConcurrency(4),
            logger);

        await emptied.Enqueue(1, TestToken);
        await emptied.ForceCancelReadersAsync(ct: TestToken);
        await emptied.Enqueue(2, TestToken);

        await emptied.WaitForIdleAsync(cancellationToken: TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        var emptyThrow = await Assert.ThrowsAsync<InvalidOperationException>(
            () => emptied.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken));
        Assert.Contains("no live readers", emptyThrow.Message, StringComparison.OrdinalIgnoreCase);

        // The log names both, in order, for the two different waits above.
        Assert.True(logger.Contains(LogLevel.Warning, "Paused"));
        Assert.True(logger.Contains(LogLevel.Warning, "NoReaders"));
    }

    [Fact]
    public async Task WaitForIdle_PausedAtConstruction_ReturnsInsteadOfHanging()
    {
        // A queue created already paused never spawns readers; pending items
        // can never be processed, so the wait must not hang for them.
        var processor = new CountingProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor).WithConcurrencyLimit(0));

        await queue.Enqueue(1, TestToken); // accepted, no readers ever

        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var completed = await Task.WhenAny(wait, Task.Delay(500, TestToken));
        Assert.True(ReferenceEquals(completed, wait));
        await wait;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WaitForIdleAsync(failIfNoProgress: true, cancellationToken: TestToken));

        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken);
        Assert.Equal(1, processor.Processed);
    }
}
