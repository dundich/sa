using System.Collections.Concurrent;

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

        var wait = queue.WaitForIdleAsync(TestToken);
        var done = await Task.WhenAny(wait, Task.Delay(2000, TestToken));
        Assert.True(ReferenceEquals(done, wait),
            "queue must reach idle once the stuck item finishes");
        await wait;

        Assert.True(queue.IsIdle());
        Assert.Equal(0, queue.QueueTasks);
        Assert.Equal(1, processor.Processed);

        queue.Dispose();
    }
}
