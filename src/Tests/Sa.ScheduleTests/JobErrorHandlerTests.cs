using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Unit tests for <see cref="JobErrorHandler"/>'s <c>StopAllJobs</c> action.
/// <para>
/// The handler runs ON the failing job's own queue reader, and that reader is still
/// processing the very item that got us here — so it must stop every job EXCEPT the
/// failing one. Stopping itself would park in <c>WaitForIdleAsync</c> waiting for its
/// own item: a guaranteed stall for the whole <c>ShutdownTimeout</c> (30 s). The
/// failing job stops itself through the abort path (the handler always throws
/// afterwards → <c>AbortedByError</c> → <c>AbortJob</c>).
/// </para>
/// No mocking framework: hand-written fakes. The self-stall regression is caught by a
/// timeout instead of hanging the suite — the fake self-job's <c>Stop()</c> never
/// completes, so an implementation that stops the failing job too cannot finish within
/// the guard delay.
/// </summary>
public sealed class JobErrorHandlerTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StopAllJobs_StopsOtherJobs_SkipsTheFailingOne_ThrowsOriginal()
    {
        FakeJobScheduler self = new(Guid.NewGuid()) { StopHangs = true };
        FakeJobScheduler other1 = new(Guid.NewGuid());
        FakeJobScheduler other2 = new(Guid.NewGuid());
        FakeScheduler scheduler = new(self, other1, other2);

        (JobErrorHandler handler, IJobContext context) = Make(scheduler, self.JobId);

        Task run = Task.Run(() =>
            handler.HandleError(context, new InvalidOperationException("boom")),
            TestContext.Current.CancellationToken);

        Task finished = await Task.WhenAny(run,
            Task.Delay(Guard, TestContext.Current.CancellationToken));

        Assert.True(
            ReferenceEquals(run, finished),
            "HandleError did not return within the guard delay — it is presumably " +
            "waiting for the failing job's own Stop(), i.e. the self-stall regression.");

        // The "action applied" contract: DoHandleError always rethrows
        // (context.LastError here is null, so the original exception comes through).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("boom", ex.Message);

        Assert.Equal(1, other1.StopCalls);
        Assert.Equal(1, other2.StopCalls);

        // The failing job must not stop itself from inside its own queue item.
        Assert.Equal(0, self.StopCalls);
    }

    [Fact]
    public async Task StopAllJobs_OtherJobStopFails_DoesNotHideTheOriginalError()
    {
        FakeJobScheduler self = new(Guid.NewGuid()) { StopHangs = true };
        FakeJobScheduler broken = new(Guid.NewGuid()) { StopFails = true };
        FakeJobScheduler healthy = new(Guid.NewGuid());
        FakeScheduler scheduler = new(self, broken, healthy);

        (JobErrorHandler handler, IJobContext context) = Make(scheduler, self.JobId);

        Task run = Task.Run(() =>
            handler.HandleError(context, new InvalidOperationException("boom")),
            TestContext.Current.CancellationToken);

        Task finished = await Task.WhenAny(run,
            Task.Delay(Guard, TestContext.Current.CancellationToken));

        Assert.True(ReferenceEquals(run, finished), "HandleError did not return within the guard delay.");

        Assert.Equal(1, healthy.StopCalls);
        Assert.Equal(1, broken.StopCalls);
        Assert.Equal(0, self.StopCalls);

        // The failure of a Stop() must be swallowed (logged) — the caller still
        // gets the original job error, not "stop failed".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void StopAllJobs_NoSchedulerRegistered_StillThrowsOriginal()
    {
        // IScheduler is absent from the job's own scope — the action must
        // degrade to a no-op stop instead of failing with NullReference/etc.
        JobSettings settings = JobSettings.Create<NoopJob>(Guid.NewGuid());
        settings.ErrorHandling.ThenStopAllJobs();

        var context = new JobContext(settings)
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider()
        };

        var handler = new JobErrorHandler(new FakeScheduleSettings(), null, null);

        // HandleError is synchronous (void) — it throws, it does not return a faulted task.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            handler.HandleError(context, new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
    }

    private static (JobErrorHandler Handler, IJobContext Context) Make(
        IScheduler scheduler, Guid failingJobId)
    {
        JobSettings settings = JobSettings.Create<NoopJob>(failingJobId);
        settings.ErrorHandling.ThenStopAllJobs();

        var services = new ServiceCollection();
        services.AddSingleton(scheduler);

        var context = new JobContext(settings)
        {
            ServiceProvider = services.BuildServiceProvider()
        };

        var handler = new JobErrorHandler(new FakeScheduleSettings(), null, null);

        return (handler, context);
    }

    sealed class NoopJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeScheduleSettings : IScheduleSettings
    {
        public bool IsHostedService => false;

        public Func<IJobContext, Exception, bool>? HandleError => null;

        public IEnumerable<IJobSettings> GetJobSettings() => [];
    }

    private sealed class FakeScheduler(params IJobScheduler[] jobs) : IScheduler
    {
        public IScheduleSettings Settings { get; } = new FakeScheduleSettings();

        public IReadOnlyCollection<IJobScheduler> Jobs { get; } = jobs;

        public Task<int> Start(CancellationToken cancellationToken) => Task.FromResult(jobs.Length);

        public Task<int> Restart(CancellationToken cancellationToken) => Task.FromResult(jobs.Length);

        public Task Stop() => Task.WhenAll(jobs.Select(job => job.Stop()));
    }

    private sealed class FakeJobScheduler(Guid jobId) : IJobScheduler
    {
        private int _stopCalls;

        public Guid JobId { get; } = jobId;

        public bool IsStarted => true;

        public int QueueTasks => 0;

        public int ConcurrencyLimit { get; set; }

        public int StopCalls => Volatile.Read(ref _stopCalls);

        /// <summary>Models a stop blocked by the caller's own in-flight queue item.</summary>
        public bool StopHangs { get; set; }

        public bool StopFails { get; set; }

        public IChangeToken StartChangeToken() => new CancellationChangeToken(CancellationToken.None);

        public Task<bool> Start(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task Stop()
        {
            Interlocked.Increment(ref _stopCalls);

            if (StopHangs) return Task.Delay(Timeout.Infinite);

            return StopFails
                ? Task.FromException(new InvalidOperationException("stop failed"))
                : Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
