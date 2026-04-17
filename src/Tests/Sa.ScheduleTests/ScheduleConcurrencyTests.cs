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
            private static readonly Lock @lock = new();

            private readonly static HashSet<SomeJob> jobs = [];
            public static int Total
            {
                get
                {
                    lock (@lock)
                    {
                        return jobs.Count;
                    }
                }
            }
            public static void Inc(SomeJob job)
            {
                lock (@lock)
                {
                    jobs.Add(job);
                }
            }

            public static void Clear()
            {
                lock (@lock) { jobs.Clear(); }
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
                    .EveryTime(TimeSpan.FromMilliseconds(10))
                    .StartImmediate()
                    .WithConcurrencyLimit(1)
                    .WithMaxConcurrency(10)
                    ;
            });
        }

        public static int Count => Counter.Total;

        public readonly static Guid JobId = Guid.NewGuid();
        public static void Reset() => Counter.Clear();
    }

    private IScheduler Sub => fixture.Sub;



    [Fact]
    public async Task Check_ExecuteCounterJob()
    {
        Fixture.Reset();

        int i = await Sub.Start(CancellationToken.None);

        Assert.NotEqual(0, i);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(1, Fixture.Count);
        Fixture.Reset();

        var j = Sub.GetSchedule(Fixture.JobId);
        Assert.NotNull(j);

        j.ConcurrencyLimit = 10;

        await Task.Delay(250, TestContext.Current.CancellationToken);

        Assert.Equal(10, Fixture.Count);

        j.ConcurrencyLimit = 4;

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Fixture.Reset();

        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(4, Fixture.Count);
    }
}
