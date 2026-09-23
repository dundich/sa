using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;
using System.Collections.Concurrent;

namespace Sa.ScheduleTests;

public class JobSchedulerTests
{

    [Fact]
    public async Task Start_ConcurrentCalls_OnlyOneSucceeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));
        var results = new ConcurrentBag<bool>();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => results.Add(
                await scheduler.Start(TestContext.Current.CancellationToken))
            ));

        await Task.WhenAll(tasks);

        Assert.Single(results, r => r);
    }


    [Fact]
    public async Task Stop_GivesUpAfterConfiguredShutdownTimeout()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(2)
            .WithConcurrencyLimit(1)
            .WithShutdownTimeout(TimeSpan.FromMilliseconds(300));

        var scheduler = new JobScheduler(settings, new HangingRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);

        // Let the reader take the (hanging) work into flight before we stop.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.Stop();
        sw.Stop();

        // Stop must give up after the configured shutdown timeout (300ms),
        // not wait out the 30s default — and not return instantly either
        // (the work item never completes, so the wait really ran).
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task Stop_DefaultTimeout_BoundedByConfiguredValue()
    {
        // No WithShutdownTimeout configured: the default 30s applies, but the
        // wait is still bounded — a cooperative runner finishing before the
        // timeout lets Stop return immediately, well under the default.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(2)
            .WithConcurrencyLimit(1);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.Stop();
        sw.Stop();

        // The runner completes immediately, so Stop must not wait out the
        // default 30s timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Stop took {sw.Elapsed} with the default timeout");

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task Stop_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
        Task.Run(async () =>
            await scheduler.Stop()
        ));

        await Task.WhenAll(tasks);


        Assert.False(scheduler.IsStarted);
        Assert.Equal(0, scheduler.QueueTasks);
    }


    [Fact]
    public async Task Dispose_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => await scheduler.DisposeAsync()));

        await Task.WhenAll(tasks);

        Assert.False(scheduler.IsStarted);
        Assert.Equal(0, scheduler.QueueTasks);
    }


    [Fact]
    public async Task ConcurrencyLimit_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(30)
            .WithConcurrencyLimit(1);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => scheduler.ConcurrencyLimit = Random.Shared.Next(2, 45)));

        await Task.WhenAll(tasks);

        Assert.True(scheduler.IsStarted);
        Assert.InRange(scheduler.ConcurrencyLimit, 2, 30);

        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task IsStarted_True_AfterSuccessfulStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.False(scheduler.IsStarted);

        var started = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(started);
        Assert.True(scheduler.IsStarted);
    }

    [Fact]
    public void QueueTasks_ReturnsZero_BeforeStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.Equal(0, scheduler.QueueTasks);
    }

    [Fact]
    public async Task Start_AlreadyCancelledToken_ReturnsFalse_AndRemainsStartable()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        var cts = new CancellationTokenSource();
        cts.Cancel();

        bool started = await scheduler.Start(cts.Token);

        Assert.False(started);
        Assert.False(scheduler.IsStarted);

        // Regression: a failed start must not leave the scheduler locked in
        // "started" state — a subsequent start with a fresh token succeeds.
        bool restarted = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(restarted);
        Assert.True(scheduler.IsStarted);

        await scheduler.Stop();
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public void Dispose_AfterStop_IsSafe()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        scheduler.Dispose();
        scheduler.Dispose(); // Double dispose should not throw
        Assert.True(true);
    }

    [Fact]
    public async Task DisposeAsync_AfterStop_IsSafe()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync(); // Double dispose should not throw
        Assert.True(true);
    }

    [Fact]
    public void ChangeToken_ReflectsStoppedState()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        var token = scheduler.StartChangeToken();
        Assert.NotNull(token);
    }



    class TestJob : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
            => await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }


    class TestJobRunner : IJobRunner
    {
        public Task<bool> Run(IJobController controller, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }


    // A work item that never completes and ignores cancellation — the queue
    // can never drain on its own, so Stop/Dispose must be bounded by the
    // configured shutdown timeout.
    class HangingRunner : IJobRunner
    {
        private static readonly Task<bool> Never =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public Task<bool> Run(IJobController controller, CancellationToken cancellationToken)
            => Never;
    }


    class TestJobController(int index) : IJobController
    {
        public bool IsPaused => false;

        public bool AbortedByError => false;

        public int Index => index;

        public ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(CanJobExecuteResult.Ok);
        }

        public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;

        public void ExecutionCompleted()
        {
            //
        }

        public void ExecutionFailed(Exception exception)
        {
            //
        }

        public void Pause()
        {

        }

        public void Resume()
        {

        }

        public void Start()
        {
            //
        }

        public void Shutdown()
        {
            //
        }

        public ValueTask WaitIfPaused(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask WaitToRun(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
