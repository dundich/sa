using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// RunOnce lifecycle regression tests: a naturally completed run-once job must
/// (a) actually stop — IsStarted must not stay stuck true over a dead job — and
/// (b) stay stopped permanently per the "stop permanently" contract: a later
/// Start() must be refused instead of re-running the job with fresh counters.
/// </summary>
public sealed class JobRunOnceLifecycleTests : IAsyncDisposable
{
    static class R
    {
        private static int _runs;

        public static int Runs => Volatile.Read(ref _runs);

        public static void Clear() => Interlocked.Exchange(ref _runs, 0);

        public static void Inc() => Interlocked.Increment(ref _runs);
    }

    sealed class OnceJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            R.Inc();
            return Task.CompletedTask;
        }
    }

    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;
    private readonly IJobScheduler _jobScheduler;

    public JobRunOnceLifecycleTests()
    {
        var jobId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddSaSchedule(b => b
            .AddJob<OnceJob>((_, job) => job.RunOnce().StartImmediate(), jobId));

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
        _jobScheduler = _scheduler.GetSchedule(jobId)!;
    }

    [Fact]
    public async Task RunOnce_RunsExactlyOnce_StopsPermanently_StartIsRefused()
    {
        R.Clear();

        Assert.Equal(1, await _scheduler.Start(TestContext.Current.CancellationToken));
        Assert.True(_jobScheduler.IsStarted);

        // Exactly one iteration, and the job stops by itself — no error, no
        // Stop() call — IsStarted must go false (before the fix it stayed true
        // forever over an already-dead job).
        await WaitForConditionAsync(() => R.Runs == 1 && !_jobScheduler.IsStarted);
        Assert.False(_jobScheduler.IsStarted);

        // "stop permanently": neither the aggregate scheduler Start() nor a
        // direct job scheduler Start() may re-run the consumed job.
        Assert.Equal(0, await _scheduler.Start(TestContext.Current.CancellationToken));
        Assert.False(await _jobScheduler.Start(TestContext.Current.CancellationToken));

        await Task.Delay(150, TestContext.Current.CancellationToken);

        Assert.Equal(1, R.Runs);
    }

    public async ValueTask DisposeAsync()
        => await _provider.DisposeAsync();

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met within the timeout");
    }
}


/// <summary>
/// The mirror case: a run-once job that FAILS through its retries and is stopped
/// by the configured error action (ThenAbortJob) is NOT a natural completion —
/// its run-once budget must NOT be consumed, so the job stays restartable, the
/// same way any error-aborted job does.
/// </summary>
public sealed class JobRunOnceErrorTests : IAsyncDisposable
{
    static class F
    {
        private static int _runs;

        public static int Runs => Volatile.Read(ref _runs);

        public static void Clear() => Interlocked.Exchange(ref _runs, 0);

        public static void Inc() => Interlocked.Increment(ref _runs);
    }

    sealed class FailingOnceJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            F.Inc();
            throw new InvalidOperationException("always fails");
        }
    }

    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;
    private readonly IJobScheduler _jobScheduler;

    public JobRunOnceErrorTests()
    {
        var jobId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddSaSchedule(b => b
            .AddJob<FailingOnceJob>((_, job) => job
                .RunOnce()
                .StartImmediate()
                .ConfigureErrorHandling(err => err
                    .IfErrorRetry(1)
                    .ThenAbortJob()), jobId));

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
        _jobScheduler = _scheduler.GetSchedule(jobId)!;
    }

    [Fact]
    public async Task RunOnce_FailingThroughRetries_AbortDoesNotConsumeTheRun()
    {
        F.Clear();

        Assert.Equal(1, await _scheduler.Start(TestContext.Current.CancellationToken));

        // 1 attempt + 1 retry, then the error action aborts the job.
        await WaitForConditionAsync(() => F.Runs >= 2 && !_jobScheduler.IsStarted);
        Assert.False(_jobScheduler.IsStarted);

        // Unlike a natural completion, the error abort leaves the job
        // restartable — the run-once budget was not consumed.
        F.Clear();
        Assert.Equal(1, await _scheduler.Start(TestContext.Current.CancellationToken));
        Assert.True(_jobScheduler.IsStarted);

        await WaitForConditionAsync(() => F.Runs >= 2);

        await _scheduler.Stop();
    }

    public async ValueTask DisposeAsync()
        => await _provider.DisposeAsync();

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "the condition was not met within the timeout");
    }
}