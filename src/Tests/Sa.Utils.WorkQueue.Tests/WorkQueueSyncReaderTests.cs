using System.Collections;
using System.Reflection;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Regression tests for readers that complete SYNCHRONOUSLY, i.e. before
/// <c>StartReaderUnderLock</c> has registered them.
/// </summary>
/// <remarks>
/// <para>
/// The dangerous shape is: a paused queue whose buffer already holds an item, plus a
/// processor that completes or faults <em>without yielding</em>. The reader started by
/// the <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/> setter then picks the item up
/// synchronously, and — if the item does not complete normally — the loop reaches its
/// <c>finally</c> and calls <c>RemoveReader</c> before the three tracking lists are
/// updated. <c>System.Threading.Lock</c> is re-entrant, so the nested call was not blocked
/// and silently mis-accounted an unregistered reader.
/// </para>
/// <para>Before the fix, all of the following held at once:</para>
/// <list type="bullet">
///   <item><c>RemoveReader</c> charged the never-registered reader a concurrency slot, so
///   the limit the caller had just set was eaten (asked 4, got 0);</item>
///   <item>the already-disposed CTS was appended to the lists anyway, so it counted as a
///   live reader forever and <c>HasLiveReaders()</c> lied;</item>
///   <item>a later limit decrease called <c>Cancel()</c> on that disposed CTS and threw
///   <see cref="ObjectDisposedException"/> out of the public setter;</item>
///   <item><c>WaitForIdleAsync</c> then waited forever for an idle that could not arrive —
///   and <c>failIfPaused: true</c> did not help, because the heuristic still saw readers;</item>
///   <item>the work CTS was never disposed, leaking its registration on the shutdown token.</item>
/// </list>
/// </remarks>
public sealed class WorkQueueSyncReaderTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>Throws without ever yielding, so the reader loop stays synchronous.</summary>
    private static Task FaultSynchronously(int input, CancellationToken ct)
        => throw new InvalidOperationException($"boom {input}");

    private static List<CancellationTokenSource> ReaderCts(object queue)
        => [.. (IEnumerable<CancellationTokenSource>)typeof(SaWorkQueue<int>)
            .GetField("_ctsReaders", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!];

    /// <summary>
    /// Waits until the reader bookkeeping reaches <paramref name="expected"/>.
    /// Cancelled readers are removed from the list by their own <c>finally</c>, so the
    /// count settles asynchronously after a limit change.
    /// </summary>
    private static async Task WaitForReaderCountAsync(SaWorkQueue<int> queue, int expected)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (ReaderCts(queue).Count != expected)
        {
            Assert.True(Environment.TickCount64 < deadline,
                $"Expected {expected} tracked reader(s), still {ReaderCts(queue).Count} after 5 s.");
            await Task.Delay(10, TestToken);
        }
    }

    /// <summary>
    /// Every tracked reader must be a usable CTS. A reader that ran to completion before
    /// it was registered leaves a disposed CTS behind, and that stale entry counts as a
    /// live reader forever.
    /// </summary>
    private static void AssertNoDisposedReaders(SaWorkQueue<int> queue)
    {
        foreach (var cts in ReaderCts(queue))
        {
            // CancellationTokenSource.Token throws ObjectDisposedException once disposed.
            var disposable = Record.Exception(() => cts.Token);
            Assert.Null(disposable);
        }
    }

    /// <summary>
    /// Faults without ever yielding (so the reader loop stays synchronous) until
    /// <see cref="ShouldFail"/> is cleared, then completes synchronously and successfully.
    /// </summary>
    private sealed class SwitchableProcessor : ISaWork<int>
    {
        private int _shouldFail = 1;

        public bool ShouldFail
        {
            get => Volatile.Read(ref _shouldFail) != 0;
            set => Volatile.Write(ref _shouldFail, value ? 1 : 0);
        }

        public Task Execute(int input, CancellationToken ct)
            => ShouldFail
                ? throw new InvalidOperationException($"boom {input}")
                : Task.CompletedTask;
    }

    /// <summary>
    /// A paused queue with a full buffer, a processor that never yields, and the given
    /// error strategy. Raising the limit starts a reader that runs to completion inline.
    /// </summary>
    private static async Task<SaWorkQueue<int>> CreateWedgedQueueAsync(
        SaExecutionErrorStrategy strategy,
        int bufferedItems = 4)
    {
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(FaultSynchronously)
                .WithConcurrencyLimit(0)
                .WithMaxConcurrency(8)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip)
                .WithShutdownTimeout(TimeSpan.FromSeconds(1))
                .WithHandleItemFaulted((_, _) => strategy));

        for (var i = 0; i < bufferedItems; i++)
        {
            await queue.Enqueue(i, TestToken);
        }

        return queue;
    }

    [Fact]
    public async Task ReArm_WithStopReader_KeepsTheRequestedLimit()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader);

        queue.ConcurrencyLimit = 4;

        // Before the fix: RemoveReader decremented for the unregistered reader, so the
        // requested limit of 4 was reported back as 0.
        Assert.Equal(4, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task ReArm_WithDefaultShutdownQueueStrategy_KeepsTheRequestedLimit()
    {
        // The default strategy (ShutdownQueue) is the one that matters: a processor that
        // throws synchronously drives it without any explicit configuration.
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.ShutdownQueue);

        queue.ConcurrencyLimit = 1;

        Assert.Equal(1, queue.ConcurrencyLimit);
        Assert.False(queue.IsEnabled); // the default strategy did shut the queue down
    }

    [Fact]
    public async Task ReArm_LeavesNoDeadReaderBehind()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader);

        queue.ConcurrencyLimit = 4;

        // Every reader that ran synchronously and died removed itself, so nothing may be
        // left in the tracking list. A stale entry would report IsCancellationRequested
        // == false (it looks alive) while its CTS is already disposed.
        Assert.Empty(ReaderCts(queue));
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task LimitDecrease_AfterSynchronousFault_DoesNotThrowObjectDisposedException()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader);

        queue.ConcurrencyLimit = 2;
        queue.ConcurrencyLimit = 0; // cancels the "live" readers

        // Before the fix: CancelReadersUnderLock called Cancel() on the disposed CTS that
        // RemoveReader had already disposed, and the ObjectDisposedException escaped the
        // public ConcurrencyLimit setter.
        Assert.Equal(0, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task WaitForIdle_AfterSynchronousFaultPool_ReapsInsteadOfHanging()
    {
        // 8 items but only 4 readers requested, so 4 items are still pending while the
        // pool is empty.
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader, bufferedItems: 8);

        queue.ConcurrencyLimit = 4;
        await Task.Delay(100, TestToken);

        // The pool is empty (every reader died synchronously) but the queue is not paused,
        // so this is the documented "no live readers" branch. Before the fix the dead
        // entries made HasLiveReaders() report > 0, so the wait never completed — the test
        // token is the only thing keeping a regression from hanging the run.
        await queue.WaitForIdleAsync(TestToken);

        Assert.False(queue.IsIdle()); // 4 items are genuinely still queued
        Assert.Equal(4, queue.QueueTasks);
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task WaitForIdle_FailIfPaused_ThrowsWhilePoolEmpty_AndRecoversAfterReArm()
    {
        // The processor has to be able to succeed eventually, otherwise restoring readers
        // just kills them again and the recovery path can never be exercised.
        var processor = new SwitchableProcessor();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(0)
                .WithMaxConcurrency(8)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip)
                .WithShutdownTimeout(TimeSpan.FromSeconds(1))
                .WithHandleItemFaulted((_, _) => SaExecutionErrorStrategy.StopReader));

        for (var i = 0; i < 8; i++)
        {
            await queue.Enqueue(i, TestToken);
        }

        queue.ConcurrencyLimit = 4; // 4 readers each fault inline and die; 4 items remain
        await Task.Delay(100, TestToken);
        Assert.Empty(ReaderCts(queue));

        // failIfPaused must see the empty pool for what it is. Before the fix the stale
        // reader entries hid it, so this reported "waiting" instead of "no progress".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WaitForIdleAsync(TestToken, failIfPaused: true));
        Assert.Contains("no live readers", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Restoring readers makes progress possible again, so the wait behaves normally.
        processor.ShouldFail = false;
        queue.ConcurrencyLimit = 4;
        await WaitForReaderCountAsync(queue, 4);
        await queue.WaitForIdleAsync(TestToken);

        Assert.True(queue.IsIdle());
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task RepeatedReArm_DoesNotAccumulateDeadReaders()
    {
        // Deterministic by construction: one item per round, one reader per round. The
        // reader picks the item up synchronously, throws and dies inline, so each round
        // must leave the tracking list empty again.
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader, bufferedItems: 5);

        for (var round = 1; round <= 5; round++)
        {
            queue.ConcurrencyLimit = 1;

            Assert.Empty(ReaderCts(queue));
            AssertNoDisposedReaders(queue);
            Assert.Equal(1, queue.ConcurrencyLimit);
            Assert.Equal(5 - round, queue.QueueTasks);

            // Returns instead of hanging, even though the pool is empty and work remains.
            await queue.WaitForIdleAsync(TestToken);
        }

        // Buffer drained: the next reader has nothing to take, so it suspends on
        // ReadAsync and stays tracked. Before the fix each round also appended a dead
        // entry, so the list grew to 10 while only 1 reader was real.
        queue.ConcurrencyLimit = 1;
        await WaitForReaderCountAsync(queue, 1);
        AssertNoDisposedReaders(queue);
        Assert.Equal(1, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task ReArm_WithSuspendingProcessor_StillRegistersTheReader()
    {
        // The healthy path: a processor that yields keeps the reader alive, so it must be
        // tracked normally. Guards against "fix" by skipping the registration too eagerly.
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken ct) =>
            {
                started.TrySetResult();
                await gate.Task.WaitAsync(ct);
            })
                .WithConcurrencyLimit(0)
                .WithMaxConcurrency(4)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip));

        await queue.Enqueue(1, TestToken);
        queue.ConcurrencyLimit = 2;

        Assert.Equal(2, ReaderCts(queue).Count);
        Assert.Equal(2, queue.ConcurrencyLimit);

        gate.TrySetResult();
        await queue.WaitForIdleAsync(TestToken);
    }

    [Fact]
    public async Task ScaleUpAndDown_WithLiveReaders_IsUnaffected()
    {
        var processed = 0;
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((int input, CancellationToken ct) =>
            {
                Interlocked.Increment(ref processed);
                return Task.CompletedTask;
            })
                .WithConcurrencyLimit(2)
                .WithMaxConcurrency(8)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip));

        for (var i = 0; i < 6; i++)
        {
            await queue.Enqueue(i, TestToken);
        }

        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(6, processed);

        queue.ConcurrencyLimit = 5;
        await WaitForReaderCountAsync(queue, 5);
        AssertNoDisposedReaders(queue);
        Assert.Equal(5, queue.ConcurrencyLimit);

        queue.ConcurrencyLimit = 1;
        await WaitForReaderCountAsync(queue, 1);
        AssertNoDisposedReaders(queue);
        Assert.Equal(1, queue.ConcurrencyLimit);

        for (var i = 0; i < 4; i++)
        {
            await queue.Enqueue(100 + i, TestToken);
        }

        queue.ConcurrencyLimit = 4;
        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(10, processed);
        await WaitForReaderCountAsync(queue, 4);
        AssertNoDisposedReaders(queue);
    }
}
