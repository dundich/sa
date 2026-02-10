using Sa.Fixture;
using Sa.Schedule;

namespace Sa.ScheduleTests;


public class SchedulePostSetupTests(SchedulePostSetupTests.Fixture fixture) : IClassFixture<SchedulePostSetupTests.Fixture>
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
                b.AddJob<SomeJob>((sp, builder) =>
                {
                    builder
                        .EveryTime(TimeSpan.FromMilliseconds(100))
                        .RunOnce()
                        .StartImmediate()
                    ;

                });
            });

            Services.AddSaSchedule(b =>
            {
                b.AddJob<SomeJob>((sp, builder) =>
                {
                    builder
                        .EveryTime(TimeSpan.FromMilliseconds(100))
                        .RunOnce()
                        .StartImmediate()
                    ;

                });
            });
        }

        public static int Count => Counter.Total;
    }

    private IScheduler Sub => fixture.Sub;


    [Fact]
    public async Task Check_Executing_RunOnce_ForMultiJobs()
    {
        int i = Sub.Start(CancellationToken.None);

        Assert.Equal(2, i);

        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(2, Fixture.Count);
    }
}
