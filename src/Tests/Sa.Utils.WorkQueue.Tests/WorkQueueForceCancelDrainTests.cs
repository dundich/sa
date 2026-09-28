using Microsoft.Extensions.Logging;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// What a force-cancel does to work that was already accepted, when the wait for the
/// cancelled readers runs out before they do.
/// </summary>
/// <remarks>
/// The chosen semantics: the buffer is kept. Nobody rejected those items, and a pool
/// re-armed afterwards can still take them, so discarding work nobody asked to discard
/// is the worse failure. The price is that the queue is then genuinely not idle — and
/// saying so is part of the contract, not an apology for it.
/// </remarks>
public sealed class WorkQueueForceCancelDrainTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole decision, end to end: the wait expires, the buffered item is not
    /// dropped, both idle APIs admit what is going on, and a re-armed pool finishes the job.
    /// </summary>
    [Fact]
    public async Task ExpiredWait_KeepsTheBuffer_AndAReArmedPoolProcessesIt()
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger<SaWorkQueue<int>>();
        var statuses = new StatusLog();
        var processor = new WedgedProcessor();

        var queue = Build(processor, clock, logger, statuses);

        try
        {
            // Item 1 is taken by the single reader and wedges there; item 2 waits behind
            // it in the buffer. Everything below is about item 2.
            _ = queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);
            var second = queue.Enqueue(2, TestToken);

            var force = Task.Run(
                () => queue.ForceCancelReadersAsync(TimeSpan.FromMinutes(5), TestToken),
                TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);
            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

            await Assert.ThrowsAsync<TimeoutException>(() => force);

            // The wait expired with the reader still inside the processor, and the
            // shortfall was announced rather than passed off in silence.
            Assert.False(processor.Left);
            Assert.True(logger.Contains(LogLevel.Warning, "Force-cancel stopped waiting"),
                "an expired force-cancel wait must say the readers did not unwind");
            Assert.True(logger.Contains(LogLevel.Warning, "were kept, not dropped"),
                "the warning must state that the buffer survived");

            // Let the abandoned reader unwind, so the pool is genuinely empty. Waiting
            // on WaitForIdleAsync is the signal itself: with the reader still live it
            // would park, and it returns only when the "no reader can drain this" branch
            // is reached — a branch that a drained buffer would never reach.
            processor.Release();
            await queue.WaitForIdleAsync(TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

            // Two APIs, same question, now consistent: work is genuinely still pending
            // and nothing is coming for it, and the one that returned says so out loud.
            Assert.False(queue.IsIdle());
            Assert.True(logger.Contains(LogLevel.Warning, "WaitForIdleAsync returned with"),
                "a WaitForIdleAsync that gave up must announce it");
            Assert.True(logger.Contains(LogLevel.Warning, "NoReaders"),
                "and must name the cause: an empty pool, not a paused queue");

            // The abandoned reader now finishes item 1, leaving exactly the buffered
            // item still owed a terminal status.
            await WaitUntilAsync(() => queue.QueueTasks == 1, TestToken);

            // And it was never marked as dropped — the proof that nothing was lost.
            Assert.False(statuses.Has(2, SaWorkStatus.Faulted));
            Assert.False(statuses.Has(2, SaWorkStatus.Aborted));

            queue.ConcurrencyLimit = 1;

            await second.AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestToken);
            await queue.WaitForIdleAsync(TestToken).WaitAsync(TimeSpan.FromSeconds(30), TestToken);

            Assert.True(queue.IsIdle());
            Assert.Equal(SaWorkStatus.Completed, statuses.Terminal(2));
            Assert.Equal(2, processor.Executions);
        }
        finally
        {
            processor.Release();
            await queue.DisposeAsync();
        }
    }

    /// <summary>
    /// The second trigger for the same hole, and the more dangerous one: a caller
    /// passing a short-lived token silently lost the drain, with nothing to notice it.
    /// </summary>
    [Fact]
    public async Task CancelledWait_KeepsTheBuffer_AndSaysSo()
    {
        var clock = new ManualTimeProvider();
        var logger = new RecordingLogger<SaWorkQueue<int>>();
        var statuses = new StatusLog();
        var processor = new WedgedProcessor();

        var queue = Build(processor, clock, logger, statuses);

        try
        {
            _ = queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);
            _ = queue.Enqueue(2, TestToken);

            using var cts = new CancellationTokenSource();
            var force = Task.Run(
                () => queue.ForceCancelReadersAsync(TimeSpan.FromMinutes(5), cts.Token),
                TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => force);

            Assert.True(logger.Contains(LogLevel.Warning, "the caller's token was cancelled"),
                "a cancelled force-cancel wait must say the readers did not unwind");
            Assert.False(statuses.Has(2, SaWorkStatus.Faulted),
                "the buffered item must not be reported as dropped");
        }
        finally
        {
            processor.Release();
            await queue.DisposeAsync();
        }
    }

    /// <summary>
    /// The boundary of the change: without an explicit timeout there is nothing to expire,
    /// so the documented force-cancel contract is untouched and the buffer is still
    /// dropped. The chosen semantics moved the exception, not the operation.
    /// </summary>
    [Fact]
    public async Task WaitThatCompletes_StillDropsTheBuffer()
    {
        var logger = new RecordingLogger<SaWorkQueue<int>>();
        var statuses = new StatusLog();
        var processor = new ReleasableProcessor();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithStatusCallback(statuses.Record),
            logger);

        _ = queue.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);
        _ = queue.Enqueue(2, TestToken);

        var force = Task.Run(() => queue.ForceCancelReadersAsync(ct: TestToken), TestToken);

        // The processor returns on its own, so the wait completes without a timeout.
        await force.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        Assert.Equal(SaWorkStatus.Faulted, statuses.Terminal(2));
        Assert.Equal(0, logger.Count(LogLevel.Warning, "Force-cancel stopped waiting"));
    }

    /// <summary>
    /// A queue that drains is not a queue in trouble, so the warning must not fire on
    /// the ordinary path. Guards against the fix being a blanket "always warn".
    /// </summary>
    [Fact]
    public async Task WaitForIdleAsync_OnADrainingQueue_StaysQuiet()
    {
        var logger = new RecordingLogger<SaWorkQueue<int>>();
        var statuses = new StatusLog();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask)
                .WithConcurrencyLimit(1)
                .WithStatusCallback(statuses.Record),
            logger);

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(TestToken);

        Assert.True(queue.IsIdle());
        Assert.Equal(0, logger.Count(LogLevel.Warning, "WaitForIdleAsync returned with"));
    }

    /// <summary>
    /// The paused branch of the same give-up, which is a deliberate state rather than a
    /// broken pool — but it is still a caller who asked "is it idle?" and got "no".
    /// </summary>
    [Fact]
    public async Task WaitForIdleAsync_WhilePaused_SaysWhyItReturned()
    {
        var logger = new RecordingLogger<SaWorkQueue<int>>();
        var statuses = new StatusLog();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (_, _) => await gate.Task.ConfigureAwait(false))
                .WithConcurrencyLimit(1)
                .WithStatusCallback(statuses.Record),
            logger);

        _ = queue.Enqueue(1, TestToken);
        _ = queue.Enqueue(2, TestToken);

        // Item 1 is in flight, item 2 is buffered, and nothing will read either.
        queue.ConcurrencyLimit = 0;
        await WaitUntilAsync(() => !queue.IsIdle(), TestToken);

        await queue.WaitForIdleAsync(TestToken);

        Assert.Equal(1, logger.Count(LogLevel.Warning, "WaitForIdleAsync returned with 2 item(s) still pending"));
        Assert.Equal(1, logger.Count(LogLevel.Warning, "Paused"));
    }

    static SaWorkQueue<int> Build(
        WedgedProcessor processor,
        ManualTimeProvider clock,
        RecordingLogger<SaWorkQueue<int>> logger,
        StatusLog statuses)
        => new(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithTimeProvider(clock)
                .WithStatusCallback(statuses.Record),
            logger);

    /// <summary>
    /// Polls a real-time condition, used only to wait for the code under test to reach
    /// its wait. Deadlines the tests care about are virtual.
    /// </summary>
    static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    /// <summary>A processor that returns when its cancellation token fires.</summary>
    private sealed class ReleasableProcessor : ISaWork<int>
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task Execute(int input, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: this is the shape of an item that unwinds promptly.
            }
        }
    }

    /// <summary>Terminal statuses per item, as the queue reported them.</summary>
    private sealed class StatusLog
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<int, SaWorkStatus> _terminal = [];

        public void Record(int input, SaWorkStatus status, Exception? error)
        {
            if (status is SaWorkStatus.Running) return;

            lock (_sync)
            {
                _terminal[input] = status;
            }
        }

        public SaWorkStatus Terminal(int input)
        {
            lock (_sync)
            {
                return _terminal.TryGetValue(input, out var status)
                    ? status
                    : throw new Xunit.Sdk.XunitException($"Item {input} never reached a terminal status.");
            }
        }

        public bool Has(int input, SaWorkStatus status)
        {
            lock (_sync)
            {
                return _terminal.TryGetValue(input, out var current) && current == status;
            }
        }
    }
}
