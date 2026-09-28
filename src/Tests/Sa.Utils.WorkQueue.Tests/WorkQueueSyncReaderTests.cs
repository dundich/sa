using System.Collections;
using System.Reflection;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Regression tests for readers that run to completion <em>synchronously</em>,
/// i.e. on the thread that started them.
/// </summary>
/// <remarks>
/// <para>
/// The shape that triggers it is ordinary: a paused queue whose buffer already
/// holds an item, plus a processor that completes or faults without yielding. The
/// reader started by the <see cref="ISaWorkQueue{TInput}.ConcurrencyLimit"/> setter
/// then picks the item up on the spot, and — if the item does not complete
/// normally — the loop reaches its <c>finally</c> and calls
/// <c>RemoveReader</c> before the loop has ever finished starting.
/// </para>
/// <para>
/// Before the registry was written before the loop was started,
/// <c>System.Threading.Lock</c> being re-entrant let that nested removal through,
/// and the accounting silently mis-handled a reader it had never seen: the
/// requested limit was eaten, a permanently disposed token was appended (it
/// reports as not cancelled, so it counted as live forever), a later limit
/// decrease threw <see cref="ObjectDisposedException"/> out of the public setter,
/// and <c>WaitForIdleAsync</c> waited forever for an idle that could not arrive.
/// </para>
/// <para>
/// That re-entrancy is no longer available even in principle: loops are launched
/// after the pool's lock is released, so a reader that removes itself on this very
/// thread takes the lock like any other caller. Registering first is still
/// load-bearing — the reader is in the registry before it can possibly leave it.
/// </para>
/// <para>
/// The point of these tests is that the failure mode is now <em>structural</em>
/// rather than guarded: there is one registration, one teardown, and no second
/// list that could drift out of step with the first.
/// </para>
/// </remarks>
public sealed class WorkQueueSyncReaderTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>Throws without ever yielding, so the reader loop stays synchronous.</summary>
    private static Task FaultSynchronously(int input, CancellationToken ct)
        => throw new InvalidOperationException($"boom {input}");

    /// <summary>
    /// Faults without ever yielding (keeping the reader loop synchronous) until
    /// <see cref="ShouldFail"/> is cleared, then completes synchronously and
    /// successfully.
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

    // --- registry introspection -------------------------------------------------
    //
    // The reader registry is a private list of a private type, so the assertions
    // below reach it by reflection. They check the two properties the registry has
    // to have: every entry is a usable reader, and none of them is a leftover.

    private static List<object> Readers(SaWorkQueue<int> queue)
        => [.. (IEnumerable)typeof(SaWorkQueue<int>)
            .GetField("_readers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(queue)!];

    private static object? Prop(object instance, string name)
        => instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(instance);

    private static int TrackedCount(SaWorkQueue<int> queue) => Readers(queue).Count;

    private static int LiveCount(SaWorkQueue<int> queue)
        => Readers(queue).Count(r => (bool)Prop(r, "IsLive")!);

    /// <summary>
    /// Waits until the registry reaches <paramref name="expected"/> entries. A
    /// cancelled reader leaves the registry from its own <c>finally</c>, so the
    /// count settles asynchronously after a limit change.
    /// </summary>
    private static async Task WaitForTrackedCountAsync(SaWorkQueue<int> queue, int expected)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (TrackedCount(queue) != expected)
        {
            Assert.True(Environment.TickCount64 < deadline,
                $"Expected {expected} tracked reader(s), still {TrackedCount(queue)} after 5 s.");
            await Task.Delay(10, TestToken);
        }
    }

    /// <summary>
    /// Every tracked reader must hold usable token sources. A reader that finished
    /// before it was registered leaves a disposed source behind, and that stale
    /// entry counts as a live reader forever.
    /// </summary>
    private static void AssertNoDisposedReaders(SaWorkQueue<int> queue)
    {
        foreach (var reader in Readers(queue))
        {
            foreach (var tokenSource in new[] { "Loop", "Work" })
            {
                var cts = (CancellationTokenSource)Prop(reader, tokenSource)!;
                // CancellationTokenSource.Token throws ObjectDisposedException once disposed.
                Assert.Null(Record.Exception(() => cts.Token));
            }
        }
    }

    /// <summary>
    /// A paused queue with a full buffer and a processor that never yields, so
    /// raising the limit starts a reader that runs to completion inline.
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
    public async Task ReArm_WithStopReader_ReportsTheCapacityItActuallyLost()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader);

        // Four buffered items, four readers requested: each reader takes one item
        // and dies on the spot, inside the setter.
        queue.ConcurrencyLimit = 4;

        // StopReader means the pool really lost four slots, so the limit honestly
        // drops to zero. What must not happen is a limit that disagrees with the
        // pool, or an entry the accounting never owned.
        Assert.Equal(0, queue.ConcurrencyLimit);
        Assert.Equal(0, LiveCount(queue));
        Assert.Equal(0, TrackedCount(queue));
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task ReArm_WithDefaultShutdownQueueStrategy_ShutsDownWithoutResidue()
    {
        // The default strategy matters: a processor that throws synchronously drives
        // it with no explicit configuration at all.
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.ShutdownQueue);

        queue.ConcurrencyLimit = 1;

        Assert.False(queue.IsEnabled);
        Assert.NotNull(queue.ShutdownError);

        // The shutdown drains what the dying reader left behind, and nothing
        // survives in the registry.
        await queue.WaitForIdleAsync(TestToken);
        Assert.Equal(0, TrackedCount(queue));
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task ReArm_LeavesNoDeadReaderBehind()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader);

        queue.ConcurrencyLimit = 4;

        // Every reader that ran synchronously and died removed itself, so the
        // registry is empty again — no permanently disposed token left behind.
        Assert.Equal(0, TrackedCount(queue));
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task LimitDecrease_AfterSynchronousFault_DoesNotThrowObjectDisposedException()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader, bufferedItems: 8);

        queue.ConcurrencyLimit = 4;
        queue.ConcurrencyLimit = 2; // re-arms four readers, which each fault and die
        queue.ConcurrencyLimit = 0;

        // Before the fix: cancelling walked a registry that held an already-disposed
        // token, and the ObjectDisposedException escaped the public setter.
        Assert.Equal(0, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task WaitForIdle_AfterSynchronousFaultPool_ReapsInsteadOfHanging()
    {
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader, bufferedItems: 8);

        queue.ConcurrencyLimit = 4; // consumes 4 of 8 items, then the pool is empty
        await Task.Delay(100, TestToken);

        // The pool is empty but the queue is not paused, so this is the documented
        // "no live readers" branch. Before the fix, a stale registry entry made the
        // pool look non-empty and the wait never completed; the test token is the
        // only thing keeping such a regression from hanging the run.
        await queue.WaitForIdleAsync(TestToken);

        Assert.False(queue.IsIdle()); // 4 items are genuinely still queued
        Assert.Equal(4, queue.QueueTasks);
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task WaitForIdle_FailIfPaused_ThrowsWhilePoolEmpty_AndRecoversAfterReArm()
    {
        // The processor has to be able to succeed eventually, otherwise restoring
        // readers just kills them again and the recovery path is unreachable.
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
        Assert.Equal(0, TrackedCount(queue));

        // failIfNoProgress has to see the empty pool for what it is. Before the fix, a
        // stale registry entry hid it and this reported "waiting" instead of
        // "no progress".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.WaitForIdleAsync(TestToken, failIfNoProgress: true));
        Assert.Contains("no live readers", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Restoring readers makes progress possible again, so the wait behaves
        // normally.
        processor.ShouldFail = false;
        queue.ConcurrencyLimit = 4;
        await WaitForTrackedCountAsync(queue, 4);
        await queue.WaitForIdleAsync(TestToken);

        Assert.True(queue.IsIdle());
        AssertNoDisposedReaders(queue);
    }

    [Fact]
    public async Task RepeatedReArm_DoesNotAccumulateDeadReaders()
    {
        // Deterministic by construction: one item per round, one reader per round.
        using var queue = await CreateWedgedQueueAsync(SaExecutionErrorStrategy.StopReader, bufferedItems: 5);

        for (var round = 1; round <= 5; round++)
        {
            queue.ConcurrencyLimit = 1;

            Assert.Equal(0, TrackedCount(queue));
            AssertNoDisposedReaders(queue);
            Assert.Equal(0, queue.ConcurrencyLimit);
            Assert.Equal(5 - round, queue.QueueTasks);

            // Returns instead of hanging, even though the pool is empty and work
            // remains.
            await queue.WaitForIdleAsync(TestToken);
        }

        // Buffer drained: the next reader has nothing to take, so it suspends and
        // stays registered. Before the fix each round also appended a dead entry,
        // so the registry grew to 10 while a single reader was real.
        queue.ConcurrencyLimit = 1;
        await WaitForTrackedCountAsync(queue, 1);
        AssertNoDisposedReaders(queue);
        Assert.Equal(1, queue.ConcurrencyLimit);
    }

    [Fact]
    public async Task ReArm_WithSuspendingProcessor_StillRegistersTheReader()
    {
        // The healthy path: a processor that yields keeps the reader alive, so it is
        // registered and observable right away.
        var gate = new TaskCompletionSource();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken ct) =>
                await gate.Task.WaitAsync(ct).ConfigureAwait(false))
                .WithConcurrencyLimit(0)
                .WithMaxConcurrency(4)
                .WithEnqueueStrategy(SaEnqueueStrategy.Skip));

        await queue.Enqueue(1, TestToken);
        queue.ConcurrencyLimit = 2;

        Assert.Equal(2, TrackedCount(queue));
        Assert.Equal(2, LiveCount(queue));
        Assert.Equal(2, queue.ConcurrencyLimit);
        AssertNoDisposedReaders(queue);

        gate.TrySetResult();
        await queue.WaitForIdleAsync(TestToken);
    }

    [Fact]
    public async Task ReArm_WhileCancellingReaders_DoesNotLetThemEatTheNewLimit()
    {
        // A limit the caller sets while readers are still dying must survive their
        // teardown. This is the accounting rule that the registry now encodes: a
        // planned stop releases its slot when the decision is taken, so the reader
        // gives nothing up a second time when it unwinds.
        var gate = new TaskCompletionSource();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken ct) =>
                await gate.Task.WaitAsync(ct).ConfigureAwait(false))
                .WithConcurrencyLimit(4)
                .WithMaxConcurrency(8));

        Assert.Equal(4, TrackedCount(queue));

        queue.ConcurrencyLimit = 1; // three readers are told to stop, but none has unwound
        Assert.Equal(1, LiveCount(queue));

        // No await in between: the three dying readers are still mid-teardown.
        queue.ConcurrencyLimit = 4;

        Assert.Equal(4, queue.ConcurrencyLimit);
        await WaitForTrackedCountAsync(queue, 4);
        Assert.Equal(4, LiveCount(queue));
        AssertNoDisposedReaders(queue);

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
        await WaitForTrackedCountAsync(queue, 5);
        AssertNoDisposedReaders(queue);
        Assert.Equal(5, queue.ConcurrencyLimit);

        queue.ConcurrencyLimit = 1;
        await WaitForTrackedCountAsync(queue, 1);
        AssertNoDisposedReaders(queue);
        Assert.Equal(1, queue.ConcurrencyLimit);

        for (var i = 0; i < 4; i++)
        {
            await queue.Enqueue(100 + i, TestToken);
        }

        queue.ConcurrencyLimit = 4;
        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(10, processed);
        await WaitForTrackedCountAsync(queue, 4);
        AssertNoDisposedReaders(queue);
    }
}
