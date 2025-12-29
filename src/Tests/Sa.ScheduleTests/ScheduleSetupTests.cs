using Sa.Fixture;
using Sa.Schedule;

namespace Sa.ScheduleTests;


public class ScheduleSetupTests(ScheduleSetupTests.Fixture fixture) : IClassFixture<ScheduleSetupTests.Fixture>
{
    public class Fixture : SaFixture<IScheduler>
    {
        static class Counter
        {
            private static int _count;
            public static int Total => _count;
            public static void Inc() => Interlocked.Increment(ref _count);
        }

        class SomeJob : IJob
        {
            public async Task Execute(IJobContext context, CancellationToken cancellationToken)
            {
                Counter.Inc();
                await Task.Delay(10, cancellationToken);
            }
        }

        public Fixture()
        {
            Services.AddSchedule(b =>
            {
                b
                    .AddJob<SomeJob>()
                    .EveryTime(TimeSpan.FromMilliseconds(100))
                    .StartImmediate()
                    ;
            });
        }

        public static int Count => Counter.Total;
    }

    private IScheduler Sub => fixture.Sub;


    [Fact]
    public async Task Check_ExecuteCounterJob()
    {
        int i = Sub.Start(CancellationToken.None);

        Assert.NotEqual(0, i);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(Fixture.Count > 0);
    }
}
