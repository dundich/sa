using Sa.Schedule.Engine;

namespace Sa.ScheduleTests;

/// <summary>
/// <see cref="JobDelay"/> must wait on the scheduler's <see cref="TimeProvider"/>, not on the
/// wall clock: the occurrence search already reads that provider's clock, so a fake or
/// simulated clock has to drive the wait as well — otherwise a test (or a host with a custom
/// clock) would still sleep for real.
/// </summary>
public sealed class JobDelayTests
{
    [Fact]
    public async Task Wait_CreatesTheTimerOnTheInjectedProvider()
    {
        var clock = new ManualTimeProvider();

        Task wait = JobDelay.Wait(TimeSpan.FromMinutes(5), clock, TestContext.Current.CancellationToken);

        // Nothing sleeps for real and the wait is not over until the provider's timer fires.
        Assert.False(wait.IsCompleted);
        Assert.Single(clock.Timers);

        clock.FireAll();

        await wait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Wait_NonPositiveDelay_IsANoop()
    {
        var clock = new ManualTimeProvider();

        await JobDelay.Wait(TimeSpan.Zero, clock, TestContext.Current.CancellationToken);
        await JobDelay.Wait(TimeSpan.FromSeconds(-1), clock, TestContext.Current.CancellationToken);

        Assert.Empty(clock.Timers);
    }

    [Fact]
    public async Task Wait_Cancellation_Throws()
    {
        var clock = new ManualTimeProvider();
        using var cts = new CancellationTokenSource();

        Task wait = JobDelay.Wait(TimeSpan.FromMinutes(5), clock, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    /// <summary>
    /// A provider whose timers only fire when the test says so, so a wait can be observed
    /// without spending the real delay.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (var timer in Timers)
                timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Fire() => callback(state);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
