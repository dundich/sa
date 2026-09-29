using System.Reflection;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Tests for the clock behind every bounded wait in the queue.
/// </summary>
/// <remarks>
/// <c>ShutdownTimeout</c> defaults to 30 seconds, and the paths that wait on it — a
/// force-cancel, a shutdown, a processor that ignores cancellation — were previously
/// reachable only by sleeping through it. With <see cref="ManualTimeProvider"/>
/// injected they are reachable instantly, and these tests prove the wait really is on
/// the injected clock: it stays parked until time is advanced, and returns as soon as
/// it is. A wall-clock implementation would have returned (or hung) either way.
/// </remarks>
public sealed class WorkQueueTimeoutTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void TimeProvider_DefaultsToTheSystemClock()
    {
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask));

        var field = typeof(SaWorkQueue<int>)
            .GetField("_timeProvider", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Same(TimeProvider.System, field.GetValue(queue));
    }

    [Fact]
    public void WithTimeProvider_Null_Throws()
    {
        var options = SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask);

        var ex = Assert.Throws<ArgumentNullException>(() => options.WithTimeProvider(null!));

        Assert.Equal("timeProvider", ex.ParamName);
    }

    [Fact]
    public async Task ForceCancelReaders_StaysParkedUntilTheInjectedClockAdvances()
    {
        var clock = new ManualTimeProvider();

        await RunWithWedgedReaderAsync(clock, async (processor, queue) =>
        {
            await queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);

            var cancel = Task.Run(() => queue.ForceCancelReaders(), TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);

            // The default timeout is 30 s and none of it has passed, because the
            // injected clock has not moved. A wall-clock wait would have been
            // satisfied by now; a bug that ignored the provider would never unblock.
            await Task.Delay(100, TestToken);
            Assert.False(cancel.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(31));
            await cancel.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

            // The processor is still inside Execute, so the wait could only have
            // ended by timing out — which is the whole point of the bound.
            Assert.False(processor.Left);
        });
    }

    [Fact]
    public async Task ForceCancelReadersAsync_ThrowsOnceTheInjectedClockPassesTheTimeout()
    {
        var clock = new ManualTimeProvider();

        await RunWithWedgedReaderAsync(clock, async (processor, queue) =>
        {
            await queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);

            // An explicit timeout, so the 30-second default is not what is under test.
            var force = Task.Run(
                () => queue.ForceCancelReadersAsync(TimeSpan.FromMinutes(5), TestToken),
                TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);
            await Task.Delay(100, TestToken);
            Assert.False(force.IsCompleted);

            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

            // What happens to the buffer when this fires is settled and covered by
            // WorkQueueForceCancelDrainTests; here the subject is only that the wait
            // is on the injected clock and honours it.
            await Assert.ThrowsAsync<TimeoutException>(() => force);
            Assert.False(processor.Left);
        });
    }

    [Fact]
    public async Task ForceCancelReadersAsync_DoesNotTimeOutWhileTimeIsFrozen()
    {
        // The mirror image of the previous test: a reader that unwinds promptly must be
        // satisfied by the reader, not by the clock. Otherwise freezing it would hang
        // the call — the failure mode a manual clock can accidentally introduce.
        var clock = new ManualTimeProvider();
        var processor = new ReleasableProcessor();

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithTimeProvider(clock));

        await queue.Enqueue(1, TestToken);
        await processor.Entered.WaitAsync(TestToken);

        var force = Task.Run(
            () => queue.ForceCancelReadersAsync(TimeSpan.FromMinutes(5), TestToken),
            TestToken);

        await force.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

        // The reader was cancelled before the wait started, so the processor returned on
        // its own. The claim is that no virtual time had to pass for the call to return;
        // whether a timer got armed before the reader unwound is a race, not a contract.
        Assert.True(processor.Left);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public async Task ShutdownAsync_ReturnsOnTheInjectedClock_WhenAReaderIgnoresCancellation()
    {
        var clock = new ManualTimeProvider();

        await RunWithWedgedReaderAsync(clock, async (processor, queue) =>
        {
            await queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);

            var shutdown = Task.Run(() => queue.ShutdownAsync(), TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);
            await Task.Delay(100, TestToken);
            Assert.False(shutdown.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(31));

            // Shutdown logs the timeout and carries on, so completing is the assertion.
            // The reader is still wedged, so the wait cannot have succeeded.
            await shutdown.WaitAsync(TimeSpan.FromSeconds(30), TestToken);
            Assert.False(processor.Left);
            Assert.False(queue.IsEnabled);
        });
    }

    [Fact]
    public async Task Shutdown_ReturnsOnTheInjectedClock_WhenAReaderIgnoresCancellation()
    {
        var clock = new ManualTimeProvider();

        await RunWithWedgedReaderAsync(clock, async (processor, queue) =>
        {
            await queue.Enqueue(1, TestToken);
            await processor.Entered.WaitAsync(TestToken);

            var shutdown = Task.Run(() => queue.Shutdown(), TestToken);

            await WaitUntilAsync(() => clock.ArmedCount > 0, TestToken);
            await Task.Delay(100, TestToken);
            Assert.False(shutdown.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(31));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(30), TestToken);

            Assert.False(processor.Left);
            Assert.False(queue.IsEnabled);
        });
    }

    /// <summary>
    /// Runs a test body against a queue whose reader is stuck in a processor, then
    /// tears both down safely.
    /// </summary>
    /// <remarks>
    /// A frozen clock makes disposal block: <c>Dispose</c> shuts down, the shutdown waits
    /// for the wedged reader, and that wait can only end on the clock — which the test
    /// has deliberately stopped. Releasing the processor first turns teardown back into
    /// the ordinary case. Doing it in a <c>finally</c> matters: a test that fails an
    /// assertion must fail, not hang, and a wedged reader would otherwise swallow the
    /// failure for ever.
    /// </remarks>
    static async Task RunWithWedgedReaderAsync(
        ManualTimeProvider clock,
        Func<WedgedProcessor, SaWorkQueue<int>, Task> body)
    {
        var processor = new WedgedProcessor();
        var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(processor)
                .WithConcurrencyLimit(1)
                .WithTimeProvider(clock));

        try
        {
            await body(processor, queue);
        }
        finally
        {
            processor.Release();
            await queue.DisposeAsync();
        }
    }

    /// <summary>
    /// Polls a real-time condition. Used only to wait for the code under test to reach
    /// its wait; every deadline the tests care about is virtual.
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

        public bool Left { get; private set; }

        public async Task Execute(int input, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Left = true;
            }
        }
    }
}
