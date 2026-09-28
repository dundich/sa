using System.Collections.Concurrent;
using System.Reflection;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Regression tests for <c>DrainAndResetIdle</c>: items left in the channel at
/// shutdown/force-cancel time must get a terminal status, and the pending
/// work count must stay honest when a reader survives the bounded wait.
/// </summary>
public sealed class WorkQueueDrainTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

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

    private sealed class NonCooperativeGateProcessor : ISaWork<int>
    {
        public readonly TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _processed;
        public int Processed => Volatile.Read(ref _processed);

        public async Task Execute(int input, CancellationToken ct)
        {
            // Deliberately ignores ct: a processor that never observes
            // cancellation must not corrupt the queue's work accounting.
            await Gate.Task;
            Interlocked.Increment(ref _processed);
        }
    }

    [Fact]
    public async Task Shutdown_ItemInChannelWithCancelledCallerToken_ReportsAborted()
    {
        var processor = new GateProcessor();
        var statuses = new ConcurrentBag<(int Item, SaWorkStatus Status)>();
        using var cts = new CancellationTokenSource();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(2)
                .WithStatusCallback((item, status, _) => statuses.Add((item, status))));

        await queue.Enqueue(1, TestToken);      // picked up by the reader, blocked on the gate
        await Task.Delay(50, TestToken);
        await queue.Enqueue(2, cts.Token);      // remains in the channel
        cts.Cancel();                           // the caller aborts the still-queued item

        await queue.ShutdownAsync();

        // The in-flight item is interrupted by the shutdown (hard mode).
        Assert.Contains(statuses, s => s.Item == 1 && s.Status == SaWorkStatus.Cancelled);

        // The queued item with a cancelled caller token must receive a terminal
        // status instead of silently disappearing from the observer.
        Assert.Contains(statuses, s => s.Item == 2 && s.Status == SaWorkStatus.Aborted);
        Assert.DoesNotContain(statuses, s => s.Item == 2 &&
            s.Status is SaWorkStatus.Running or SaWorkStatus.Completed or SaWorkStatus.Faulted);

        // Every accepted item was accounted for.
        Assert.True(queue.IsIdle());
        Assert.Equal(0, queue.QueueTasks);
    }

    [Fact]
    public async Task Shutdown_StuckReader_KeepsIsIdleFalseUntilItemFinishes()
    {
        var processor = new NonCooperativeGateProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(1)
                .WithShutdownTimeout(TimeSpan.FromMilliseconds(300)));

        await queue.Enqueue(1, CancellationToken.None);
        await Task.Delay(50, TestToken); // reader blocks on the gate and ignores the shutdown

        await queue.ShutdownAsync(); // the bounded reader wait times out; shutdown still completes

        // The stuck reader still holds an active item: the queue must not
        // claim idle while work is genuinely in flight.
        Assert.False(queue.IsIdle());
        Assert.Equal(1, queue.QueueTasks);

        processor.Gate.TrySetResult(); // let the stuck item finish

        var wait = queue.WaitForIdleAsync(cancellationToken: TestToken);
        var done = await Task.WhenAny(wait, Task.Delay(2000, TestToken));
        Assert.True(ReferenceEquals(done, wait),
            "queue must reach idle once the stuck item finishes");
        await wait;

        Assert.True(queue.IsIdle());
        Assert.Equal(0, queue.QueueTasks);
        Assert.Equal(1, processor.Processed);

        queue.Dispose();
    }

    [Fact]
    public async Task ForceCancelReaders_DroppedItems_ReportForceCancelNotShutdown()
    {
        var processor = new GateProcessor();
        var dropped = new ConcurrentDictionary<int, string>();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithStatusCallback((item, status, ex) =>
                {
                    if (status == SaWorkStatus.Faulted)
                    {
                        dropped[item] = ex?.Message ?? "";
                    }
                }));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken); // item 1 in flight, blocked on the gate
        for (var i = 2; i <= 5; i++)
        {
            await queue.Enqueue(i, TestToken); // items 2..5 stay in the channel
        }

        await queue.ForceCancelReadersAsync(ct: TestToken);

        // The queue stays Active after an emergency stop, so the dropped items
        // must not be blamed on a shutdown that never happened. The status
        // stays Faulted (consumers already treat it as an error); only the
        // exception text must match the cause.
        Assert.True(queue.IsEnabled, "force-cancel must not disable the queue");
        Assert.Equal(4, dropped.Count); // items 2..5
        foreach (var message in dropped.Values)
        {
            Assert.Contains("force-cancel", message);
            Assert.DoesNotContain("shut down", message);
        }

        // The in-flight item was interrupted by the hard-mode cancel, not
        // dropped by the drain: it must not appear in the drop set.
        Assert.False(dropped.ContainsKey(1));
    }

    [Fact]
    public async Task Shutdown_FaultingCancellationCallback_StillCompletesWriterAndDrains()
    {
        // A cancellation callback that throws used to short-circuit the whole
        // shutdown (B4): the writer was never completed, the buffer was never
        // drained, IsIdle() stayed false forever, and a producer parked in
        // WriteAsync (Wait strategy) never completed. Each step now has its
        // own try/catch, so the failure is logged and shutdown still finishes.
        var processor = new GateProcessor();
        var statuses = new ConcurrentDictionary<int, SaWorkStatus>();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithQueueCapacity(8)
                .WithShutdownTimeout(TimeSpan.FromMilliseconds(300))
                .WithStatusCallback((item, status, _) => statuses[item] = status));

        await queue.Enqueue(1, TestToken);
        await Task.Delay(50, TestToken); // item 1 in flight, blocked on the gate
        for (var i = 2; i <= 5; i++)
        {
            await queue.Enqueue(i, TestToken); // items 2..5 stay in the channel
        }

        // The public API offers no way to register a callback on the shutdown
        // CTS itself, so the fault is injected through the private field:
        // ShutdownAsync's CancelAsync must see the throwing callback.
        var cts = (CancellationTokenSource)typeof(SaWorkQueue<int>)
            .GetField("_shutdownCts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(queue)!;
        cts.Token.Register(() => throw new InvalidOperationException("boom"));

        await queue.ShutdownAsync(); // must not throw

        Assert.False(queue.IsEnabled);
        Assert.True(queue.IsIdle(),
            "the drain must reset the pending count even when the cancel step faulted");
        Assert.Equal(0, queue.QueueTasks);

        // The in-flight item was interrupted; the buffered items were drained
        // as Faulted — none of them silently disappeared.
        Assert.Equal(SaWorkStatus.Cancelled, statuses[1]);
        for (var i = 2; i <= 5; i++)
        {
            Assert.Equal(SaWorkStatus.Faulted, statuses[i]);
        }
    }
}
