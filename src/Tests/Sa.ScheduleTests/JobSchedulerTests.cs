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
        Assert.Equal(0, scheduler.ActiveTasks);
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
        Assert.Equal(0, scheduler.ActiveTasks);
    }


    [Fact]
    public async Task ConcurrencyLimit_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrencyLimit(30)
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
    public void ActiveTasks_ReturnsZero_BeforeStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.Equal(0, scheduler.ActiveTasks);
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
        public Task Run(IJobController controller, CancellationToken cancellationToken) => Task.CompletedTask;
    }


    class TestJobController(int index) : IJobController
    {
        public bool IsPaused => false;

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
