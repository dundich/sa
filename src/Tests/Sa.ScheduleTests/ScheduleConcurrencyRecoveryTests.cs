using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// Regression: lowering <c>ConcurrencyLimit</c> must pause slots, never destroy them.
/// <para>
/// The scheduler used to resize the work queue to the effective limit. Removing a reader
/// hard-cancels its in-flight item, and for the scheduler that item is the job's whole
/// loop — so every slot above the new limit was gone permanently: raising the limit again
/// restored nothing until a full <c>Stop</c>+<c>Start</c>. The reader pool is now pinned at
/// <c>MaxConcurrency</c> and the limit is applied through the controller pause gate.
/// </para>
/// The job blocks inside <c>Execute</c> on a shared semaphore, which makes the number of
/// active slots observable without timing-based assertions.
/// </summary>
public sealed class ScheduleConcurrencyRecoveryTests : IAsyncDisposable
{
    private static class Probe
    {
        private static int _active;

        public static int Active => Volatile.Read(ref _active);

        public static void Enter() => Interlocked.Increment(ref _active);

        public static void Exit() => Interlocked.Decrement(ref _active);

        public static void Reset() => Volatile.Write(ref _active, 0);
    }

    private sealed class GatedJob(SemaphoreSlim gate) : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            Probe.Enter();

            try
            {
                await gate.WaitAsync(cancellationToken);
            }
            finally
            {
                Probe.Exit();
            }
        }
    }

    private readonly SemaphoreSlim _gate = new(0);
    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;
    private readonly IJobScheduler _job;
    private readonly Guid _jobId = Guid.NewGuid();

    public ScheduleConcurrencyRecoveryTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_gate);
        services.AddSaSchedule(b => b
            .AddJob<GatedJob>((_, job) => job
                .EveryTime(TimeSpan.FromMilliseconds(20))
                .StartImmediate()
                .WithConcurrencyLimit(3)
                .WithMaxConcurrency(3), _jobId));

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
        _job = _scheduler.GetSchedule(_jobId)!;
    }

    [Fact]
    public async Task LoweringLimit_PausesSlots_RaisingItRestoresTheirConcurrency()
    {
        Probe.Reset();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(1, await _scheduler.Start(ct));

        // All three pre-allocated slots are live from the start and blocked in Execute.
        await WaitForAsync(() => Probe.Active == 3, ct);
        Assert.Equal(3, _job.QueueTasks);

        // Lower the limit while every slot is mid-iteration.
        _job.ConcurrencyLimit = 1;
        Assert.Equal(1, _job.ConcurrencyLimit);

        // The slot items stay in the queue — they are paused, not torn down. (Before
        // the fix the queue shrank to 1 and the two removed readers hard-cancelled
        // their in-flight job loop, so those slots were gone for good.)
        Assert.Equal(3, _job.QueueTasks);

        // Let the three in-flight iterations finish: exactly one slot may continue,
        // the other two park on the pause gate.
        _gate.Release(3);
        await WaitForAsync(() => Probe.Active == 1, ct);
        await Task.Delay(150, ct);
        Assert.Equal(1, Probe.Active);

        // Raising the limit again resumes the paused slots — no restart involved.
        _job.ConcurrencyLimit = 3;
        await WaitForAsync(() => Probe.Active == 3, ct);

        Assert.Equal(3, Probe.Active);
        Assert.True(_job.IsStarted);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop cancels the blocked iterations; the gate is only released so a
        // cooperative job does not rely on cancellation to unwind.
        _gate.Release(8);
        await _provider.DisposeAsync();
        _gate.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25, cancellationToken);
        }

        Assert.True(condition(), "the condition was not met within the timeout");
    }
}
