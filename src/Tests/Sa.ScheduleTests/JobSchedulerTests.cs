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

        public void Init()
        {
            //
        }

        public void Finish()
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
