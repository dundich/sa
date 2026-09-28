namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// What a status callback may assume about the queue that invokes it.
/// </summary>
/// <remarks>
/// The callback is the one place where caller code runs inside the queue's own
/// machinery, and a reader that finds work waiting runs that work on whichever
/// thread resized the pool. So the callback runs on the reader's thread, which for a
/// limit change is the caller's — the queue does not hide that behind a hop.
/// <para>
/// The property this file protects: <em>no queue lock is held</em> while the callback
/// runs. The register/launch split in <c>StartReaders</c> exists for that reason. A
/// callback that hands its event to another thread — a logger, a metrics sink, any
/// other service — and waits for it, deadlocked against the very thread that was
/// resizing the pool.
/// </para>
/// </remarks>
public sealed class WorkQueueStatusCallbackTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// The deadlock this file exists for, in the shape that triggered it: a paused
    /// queue with a buffered item, a processor that never yields, and a callback that
    /// needs the pool from another thread.
    /// </summary>
    [Fact]
    public async Task CallbackFromALaunchedReader_CanUseTheQueueFromAnotherThread()
    {
        var probe = new PoolAccessProbe();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(static (_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(0)
                .WithStatusCallback(probe.OnStatus));

        // Paused, so the item waits in the buffer. The reader that the limit change
        // below starts will find it there and take it without yielding.
        _ = queue.Enqueue(1, TestToken);
        await WaitUntilAsync(() => queue.QueueTasks == 1, TestToken);

        probe.Arm(queue);
        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        Assert.True(queue.IsIdle());
        Assert.True(probe.Ran, "the status callback never ran, so this proved nothing");
        Assert.True(probe.OtherThreadGotIn,
            "the status callback ran with the pool's reader lock held: another thread " +
            "asking for the pool could not get in while the callback waited for it");
    }

    /// <summary>
    /// A guard on the other half of the change, and one this file's sibling
    /// regression tests rely on: moving the launch out of the lock must not also move
    /// it onto a thread pool. A reader still runs its first item on the thread that
    /// started it, which is what makes "runs until the first real yield" a fact about
    /// the caller's thread rather than a race.
    /// </summary>
    /// <remarks>
    /// Passes before the change as well — it pins a property the fix could have
    /// broken, not a defect the fix removed.
    /// </remarks>
    [Fact]
    public async Task LaunchedReader_ReportsOnTheThreadThatResizedThePool()
    {
        int? firstReportThread = null;

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(static (_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(0)
                .WithStatusCallback((_, _, _) => firstReportThread ??= Environment.CurrentManagedThreadId));

        _ = queue.Enqueue(1, TestToken);
        await WaitUntilAsync(() => queue.QueueTasks == 1, TestToken);

        var resizingThread = Environment.CurrentManagedThreadId;
        queue.ConcurrencyLimit = 1;
        await queue.WaitForIdleAsync(cancellationToken: TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        Assert.Equal(resizingThread, firstReportThread);
    }

    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    /// <summary>
    /// From inside the callback, has another thread completed a pool operation, and
    /// did the callback run at all.
    /// </summary>
    /// <remarks>
    /// A thread-blocking wait, deliberately, and bounded. The regression it watches
    /// for cannot be observed any other way — a lock this thread already holds does
    /// not fail, it hangs — so the probe has to be willing to give up and report
    /// "no" rather than take the whole test run down with it.
    /// </remarks>
    private sealed class PoolAccessProbe
    {
        private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private SaWorkQueue<int>? _queue;
        private int _armed;
        private volatile bool _ran;
        private volatile bool _otherThreadGotIn;

        /// <summary>Whether the callback was invoked at all after <see cref="Arm"/>.</summary>
        public bool Ran => _ran;

        /// <summary>
        /// Whether a thread other than the one running the callback managed to take
        /// the pool's reader lock and assign the limit while the callback waited.
        /// </summary>
        public bool OtherThreadGotIn => _otherThreadGotIn;

        public void Arm(SaWorkQueue<int> queue)
        {
            _queue = queue;
            Volatile.Write(ref _armed, 1);
        }

        public void OnStatus(int input, SaWorkStatus status, Exception? error)
        {
            var queue = _queue;
            if (queue is null) return;
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;

            _ran = true;

            // Assigning the limit goes through the pool's reader lock. From the
            // callback's point of view this is the most ordinary thing an observer
            // can do — and the one thing it could not do.
            var worker = Task.Run(() => queue.ConcurrencyLimit = 1);

            // A queued-but-unstarted worker would hide a deadlock behind a slow
            // machine, so this waits for the worker itself, not for a slot.
            _otherThreadGotIn = worker.Wait(Deadline);
        }
    }
}
