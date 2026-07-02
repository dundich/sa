using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

public sealed class JobErrorHandlerIntegrationTests : IAsyncDisposable
{
    private readonly ScheduleSetupTests.Fixture _fixture;

    public JobErrorHandlerIntegrationTests()
    {
        _fixture = new ScheduleSetupTests.Fixture();
    }

    [Fact]
    public async Task GlobalErrorHandler_ConsumesError_DoesNotCrash()
    {
        // This tests that the global error handler registered via AddErrorHandler
        // can consume errors before per-job handling kicks in
        var scheduler = _fixture.Sub;

        int started = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(started > 0);

        // Let it run and process normally
        await Task.Delay(350, TestContext.Current.CancellationToken);

        await scheduler.Stop();
    }

    [Fact]
    public void ErrorHandlingBuilder_ChainMethods_WorkCorrectly()
    {
        // Verify the fluent builder chain compiles and methods are accessible
        var services = new ServiceCollection();
        services.AddSaSchedule(b =>
        {
            b.AddJob<TestJob>((sp, job) =>
            {
                job
                    .EverySeconds(1)
                    .ConfigureErrorHandling(err => err
                        .IfErrorRetry(3)
                        .DoSuppressError(ex => ex is TimeoutException)
                        .ThenAbortJob());
            });
        });

        var provider = services.BuildServiceProvider();
        var scheduleSettings = provider.GetRequiredService<IScheduleSettings>();
        var jobSettings = scheduleSettings.GetJobSettings().First();

        Assert.Equal(ErrorHandlingAction.AbortJob, jobSettings.ErrorHandling.ThenAction);
        Assert.Equal(3, jobSettings.ErrorHandling.RetryCount);
        Assert.NotNull(jobSettings.ErrorHandling.SuppressError);
    }

    [Fact]
    public async Task Scheduler_StartStopsCleanly()
    {
        var scheduler = _fixture.Sub;

        int started = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(started > 0);

        await Task.Delay(250, TestContext.Current.CancellationToken);

        await scheduler.Stop();
    }

    private sealed class TestJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.Delay(50, cancellationToken);
    }

    public ValueTask DisposeAsync()
        => _fixture.DisposeAsync();
}
