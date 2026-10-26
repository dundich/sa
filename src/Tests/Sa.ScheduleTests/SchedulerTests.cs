using Microsoft.Extensions.Primitives;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// <see cref="Scheduler"/>-level behaviour that does not need a live job: the aggregate
/// operations report what actually happened instead of what was attempted.
/// </summary>
public sealed class SchedulerTests
{
    [Fact]
    public async Task Restart_CountsOnlyJobsThatActuallyRestarted_AndSkipsStoppedOnes()
    {
        var restarted = new StubJobScheduler { IsStarted = true, StartsAgain = true };
        var refusing = new StubJobScheduler { IsStarted = true, StartsAgain = false };
        var stopped = new StubJobScheduler { IsStarted = false, StartsAgain = true };

        var scheduler = CreateScheduler(restarted, refusing, stopped);

        int count = await scheduler.Restart(TestContext.Current.CancellationToken);

        // Previously every previously-started job counted as "restarted" even when its
        // Start() returned false.
        Assert.Equal(1, count);

        Assert.Equal(1, restarted.StopCalls);
        Assert.Equal(1, restarted.StartCalls);

        Assert.Equal(1, refusing.StopCalls);
        Assert.Equal(1, refusing.StartCalls);

        // A stopped job (error-aborted, RunOnce consumed) is not part of Restart.
        Assert.Equal(0, stopped.StopCalls);
        Assert.Equal(0, stopped.StartCalls);
    }

    [Fact]
    public async Task Restart_OneJobThrowing_DoesNotPreventTheOthers()
    {
        // A broken job's teardown (or start) must not abort the restart of the others:
        // the failure is logged and counted as "not restarted".
        var stopFails = new StubJobScheduler { IsStarted = true, StopThrows = true };
        var startFails = new StubJobScheduler { IsStarted = true, StartThrows = true };
        var healthy = new StubJobScheduler { IsStarted = true, StartsAgain = true };

        var scheduler = CreateScheduler(stopFails, startFails, healthy);

        Assert.Equal(1, await scheduler.Restart(TestContext.Current.CancellationToken));

        Assert.Equal(1, stopFails.StopCalls);
        Assert.Equal(0, stopFails.StartCalls);

        Assert.Equal(1, startFails.StopCalls);
        Assert.Equal(1, startFails.StartCalls);

        Assert.Equal(1, healthy.StopCalls);
        Assert.Equal(1, healthy.StartCalls);
        Assert.True(healthy.IsStarted);
    }

    [Fact]
    public async Task Restart_NoRunningJobs_ReturnsZero()
    {
        var scheduler = CreateScheduler(new StubJobScheduler { IsStarted = false });

        Assert.Equal(0, await scheduler.Restart(TestContext.Current.CancellationToken));
    }

    private static Scheduler CreateScheduler(params StubJobScheduler[] jobs)
    {
        IJobSettings[] settings = [.. jobs.Select(_ => JobSettings.Create<TestJob>(Guid.NewGuid()))];
        int index = -1;

        return new Scheduler(
            new StubScheduleSettings(settings),
            new StubJobFactory(() => jobs[Interlocked.Increment(ref index)]));
    }

    private sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubScheduleSettings(IReadOnlyList<IJobSettings> jobs) : IScheduleSettings
    {
        public bool IsHostedService => false;

        public Func<IJobContext, Exception, bool>? HandleError => null;

        public IEnumerable<IJobSettings> GetJobSettings() => jobs;
    }

    private sealed class StubJobFactory(Func<IJobScheduler> next) : IJobFactory
    {
        public IJobScheduler CreateJobSchedule(IJobSettings settings) => next();
    }

    private sealed class StubJobScheduler : IJobScheduler
    {
        public Guid JobId { get; } = Guid.NewGuid();

        public bool IsStarted { get; set; }

        public bool StartsAgain { get; init; } = true;

        public bool StopThrows { get; init; }

        public bool StartThrows { get; init; }

        public int QueueTasks => 0;

        public int ConcurrencyLimit { get; set; } = 1;

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public IChangeToken StartChangeToken() => new CancellationChangeToken(CancellationToken.None);

        public Task<bool> Start(CancellationToken cancellationToken)
        {
            StartCalls++;

            if (StartThrows)
            {
                throw new InvalidOperationException("start failed");
            }

            IsStarted = StartsAgain;
            return Task.FromResult(StartsAgain);
        }

        public Task Stop()
        {
            StopCalls++;

            if (StopThrows)
            {
                throw new InvalidOperationException("stop failed");
            }

            IsStarted = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
