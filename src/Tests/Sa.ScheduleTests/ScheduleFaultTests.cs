using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// Regression: a slot that faults outside the iteration loop (here: the job cannot even
/// be constructed) used to fault the queue item only. The queue drops the reader and the
/// scheduler kept reporting <c>IsStarted == true</c> over a dead job — with the default
/// <c>MaxConcurrency == 1</c> that means the job never runs again and nothing says so.
/// The fault is now logged and goes through the same teardown as a finished loop.
/// </summary>
public sealed class ScheduleFaultTests : IAsyncDisposable
{
    private readonly Guid _jobId = Guid.NewGuid();
    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;
    private readonly IJobScheduler _job;

    public ScheduleFaultTests()
    {
        ExplodingJob.Attempts = 0;

        var services = new ServiceCollection();
        services.AddSaSchedule(b => b
            .AddJob<ExplodingJob>((_, job) => job.StartImmediate(), _jobId));

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
        _job = _scheduler.GetSchedule(_jobId)!;
    }

    [Fact]
    public async Task JobWhoseConstructorThrows_StopsInsteadOfReportingStarted()
    {
        // Start may report true (the slots spawned, then the fault landed) or false
        // (the fault landed while starting) — both are fine. What must never happen
        // is a scheduler that reports a running job that is dead.
        _ = await _scheduler.Start(TestContext.Current.CancellationToken);

        await WaitForAsync(() =>
            Volatile.Read(ref ExplodingJob.Attempts) >= 1 && !_job.IsStarted);

        Assert.False(_job.IsStarted, "IsStarted stayed true over a job that never started");

        // Teardown must stay safe after a failed start.
        await _scheduler.Stop();

        Assert.False(_job.IsStarted);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met within the timeout");
    }

    private sealed class ExplodingJob : IJob
    {
        public static int Attempts;

        public ExplodingJob()
        {
            Interlocked.Increment(ref Attempts);
            throw new InvalidOperationException("job constructor failed");
        }

        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
