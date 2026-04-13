using Sa.Fixture;
using Sa.Schedule;

namespace Sa.ScheduleTests;



public sealed class ScheduleConcurrencyTests(ScheduleConcurrencyTests.Fixture fixture)
    : IClassFixture<ScheduleConcurrencyTests.Fixture>
{
    public class Fixture : SaFixture<IScheduler>
    {
        static class Counter
        {
            private readonly static HashSet<SomeJob> jobs = [];
            public static int Total => jobs.Count;
            public static void Inc(SomeJob job)
            {
                jobs.Add(job);
            }
        }

        class SomeJob : IJob
        {
            public async Task Execute(IJobContext context, CancellationToken cancellationToken)
            {
                Counter.Inc(this);
                await Task.Delay(10, cancellationToken);
            }
        }

        public Fixture()
        {
            Services.AddSaSchedule(b =>
            {
                b
                    .AddJob<SomeJob>(JobId)
                    .EveryTime(TimeSpan.FromMilliseconds(50))
                    .StartImmediate()
                    .WithConcurrencyLimit(1)
                    ;
            });
        }

        public static int Count => Counter.Total;

        public readonly static Guid JobId = Guid.NewGuid();
    }

    private IScheduler Sub => fixture.Sub;



    [Fact]
    public async Task Check_ExecuteCounterJob()
    {
        int i = await Sub.Start(CancellationToken.None);

        Assert.NotEqual(0, i);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, Fixture.Count);

        var j = Sub.GetSchedule(Fixture.JobId);

        Assert.NotNull(j);

        j.ConcurrencyLimit = 10;

        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(11, Fixture.Count);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(11, Fixture.Count);

        j.ConcurrencyLimit = 4;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(15, Fixture.Count);
    }
}
