using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// Regression test for the error-abort lifecycle: when retries are exhausted
/// and <c>ThenAbortJob</c> is configured, the job must stop, but the underlying
/// work queue must stay alive, so the job can be restarted via Stop() + Start().
/// (Before the fix the error handler's throw reached the work queue and
/// permanently shut it down, so the restart failed.)
/// </summary>
public sealed class JobAbortIntegrationTests : IAsyncDisposable
{
    static class Counter
    {
        private static int _count;

        public static int Total => Volatile.Read(ref _count);

        public static void Inc() => Interlocked.Increment(ref _count);

        public static void Clear() => Interlocked.Exchange(ref _count, 0);
    }

    sealed class FailingJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            Counter.Inc();
            throw new InvalidOperationException("always fails");
        }
    }

    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;
    private readonly IJobScheduler _jobScheduler;

    public JobAbortIntegrationTests()
    {
        var services = new ServiceCollection();

        var jobId = Guid.NewGuid();

        services.AddSaSchedule(b =>
        {
            b.AddJob<FailingJob>((sp, job) =>
            {
                job
                    .EveryTime(TimeSpan.FromMilliseconds(10))
                    .StartImmediate()
                    .WithConcurrencyLimit(1)
                    .WithMaxConcurrency(2)
                    .ConfigureErrorHandling(err => err
                        .IfErrorRetry(1)
                        .ThenAbortJob());
            }, jobId);
        });

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
        _jobScheduler = _scheduler.GetSchedule(jobId)!;
    }

    [Fact]
    public async Task RetriesExhausted_AbortStopsJob_QueueStaysAlive_JobIsRestartable()
    {
        Counter.Clear();

        Assert.Equal(1, await _scheduler.Start(TestContext.Current.CancellationToken));
        Assert.True(_jobScheduler.IsStarted);

        // The job runs once, retries once, then the error handler aborts it.
        // The job always fails, so no more than 2 executions can happen.
        await WaitForConditionAsync(() => Counter.Total >= 2);
        var stable = await WaitForStableCounterAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, stable);
        Assert.False(_jobScheduler.IsStarted);

        // Stop after an abort is a no-op, and the job must remain restartable:
        // the queue survived the abort, so Start() works again.
        await _scheduler.Stop();

        Counter.Clear();
        Assert.Equal(1, await _scheduler.Start(TestContext.Current.CancellationToken));
        Assert.True(_jobScheduler.IsStarted);

        // The restarted job runs again (and eventually aborts again, which is fine).
        await WaitForConditionAsync(() => Counter.Total >= 2);

        await _scheduler.Stop();
    }

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

    private static async Task<int> WaitForStableCounterAsync(TimeSpan timeout)
    {
        var last = -1;
        var stableSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + timeout;
        var current = -1;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);

            current = Counter.Total;

            if (current == last)
            {
                // counter has been flat for 300ms — the job has stopped
                if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(300))
                    return current;
            }
            else
            {
                last = current;
                stableSince = DateTime.UtcNow;
            }
        }

        Assert.Fail($"job did not stop within {timeout.TotalSeconds:0}s — counter is still changing (last={current}); the abort did not take effect");
        return -1;
    }

    public async ValueTask DisposeAsync()
        => await _provider.DisposeAsync();
}
