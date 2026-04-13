using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;
using System.Collections.Concurrent;

namespace Sa.ScheduleTests;

public class JobSchedulerTests
{

    class TestJob : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
            => await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }


    class TestJobRunner : IJobRunner
    {
        public Task Run(IJobController controller, CancellationToken cancellationToken) => Task.CompletedTask;
    }


    class TestJobController : IJobController
    {
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

        public void Running()
        {
            //
        }

        public void Stopped(TaskStatus status)
        {
            //
        }

        public ValueTask WaitToRun(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }


    [Fact]
    public async Task Start_ConcurrentCalls_OnlyOneSucceeds()
    {

        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());


        var scheduler = new JobScheduler(settings, new TestJobRunner(), () => new TestJobController());
        var results = new ConcurrentBag<bool>();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => results.Add(
                await scheduler.Start(TestContext.Current.CancellationToken))
            ));

        await Task.WhenAll(tasks);

        Assert.Single(results, r => r);
        Assert.Equal(9, results.Count(r => !r));
    }
}
