using Sa.Fixture;
using Sa.Schedule;

namespace Sa.ScheduleTests;


public sealed class ScheduleSetupTests(ScheduleSetupTests.Fixture fixture)
    : IClassFixture<ScheduleSetupTests.Fixture>
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
            Services.AddSaSchedule(b =>
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
        int started = await Sub.Start(CancellationToken.None);

        Assert.Equal(1, started);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Job should have executed multiple times (100ms interval, 300ms runtime)
        Assert.InRange(Fixture.Count, 2, 10);
    }
}
