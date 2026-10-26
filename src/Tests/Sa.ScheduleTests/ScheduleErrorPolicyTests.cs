using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// Regression for the default (unkeyed) schedule's <c>IJobErrorHandler</c> registration.
/// <para>
/// It used to be registered by implementation type, so the DI container needed both
/// <c>ILogger&lt;JobErrorHandler&gt;</c> and <c>IHostApplicationLifetime</c> to construct it —
/// a nullable annotation is not a default value. In a bare <see cref="ServiceCollection"/>
/// (every non-Generic-Host consumer, including <c>Sa.Fixture</c>, which registers logging but
/// no lifetime) resolution threw; <c>JobController</c>'s error path swallowed that and set
/// <c>AbortedByError</c>, so the global handler and the configured action
/// (<c>ThenCloseApplication</c> / <c>ThenStopAllJobs</c>) were silently bypassed and the job
/// simply stopped. Named schedules already used a factory with <c>GetService</c> and never
/// had the bug.
/// </para>
/// </summary>
public sealed class ScheduleErrorPolicyTests
{
    [Fact]
    public async Task DefaultSchedule_GlobalErrorHandler_RunsWithoutAGenericHost()
    {
        int handlerCalls = 0;

        var services = new ServiceCollection();
        services.AddSaSchedule(b =>
        {
            b.AddErrorHandler((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return false; // let the per-job policy (ThenAbortJob) decide
            });
            b.AddJob<FailingJob>((_, job) => job
                .StartImmediate()
                .ConfigureErrorHandling(err => err
                    .IfErrorRetry(0)
                    .ThenAbortJob()));
        });

        await using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<IScheduler>();

        Assert.Equal(1, await scheduler.Start(TestContext.Current.CancellationToken));

        await WaitForAsync(() => Volatile.Read(ref handlerCalls) >= 1);

        // The handler ran, so the policy was applied through the normal path rather
        // than being bypassed by an unresolvable handler.
        Assert.True(Volatile.Read(ref handlerCalls) >= 1);

        await scheduler.Stop();
    }

    [Fact]
    public async Task DefaultSchedule_ThenCloseApplication_StopsTheApplicationWithoutALogger()
    {
        // Only the lifetime is registered — no ILogger<>. Before the fix, resolving
        // JobErrorHandler failed on the logger parameter, so StopApplication was never
        // called and the job aborted with no trace of the configured action.
        var lifetime = new RecordingLifetime();

        var services = new ServiceCollection();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSaSchedule(b => b
            .AddJob<FailingJob>((_, job) => job
                .StartImmediate()
                .ConfigureErrorHandling(err => err
                    .IfErrorRetry(0)
                    .ThenCloseApplication())));

        await using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<IScheduler>();

        Assert.Equal(1, await scheduler.Start(TestContext.Current.CancellationToken));

        await WaitForAsync(() => lifetime.StopRequests > 0);

        Assert.True(lifetime.StopRequests > 0, "ThenCloseApplication never reached the lifetime");

        await scheduler.Stop();
    }

    [Fact]
    public async Task DefaultSchedule_GlobalErrorHandler_ReceivesTheOriginalException()
    {
        // The handler sees the exception the job threw (same object retry/suppress
        // see); the JobException wrapper is available as IJobContext.LastError.
        var seen = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddSaSchedule(b =>
        {
            b.AddErrorHandler((_, ex) =>
            {
                seen.TrySetResult(ex);
                return false; // let the per-job policy (ThenAbortJob) decide
            });
            b.AddJob<FailingJob>((_, job) => job
                .StartImmediate()
                .ConfigureErrorHandling(err => err
                    .IfErrorRetry(0)
                    .ThenAbortJob()));
        });

        await using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredService<IScheduler>();

        Assert.Equal(1, await scheduler.Start(TestContext.Current.CancellationToken));

        Exception error = await seen.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.IsType<InvalidOperationException>(error);

        await scheduler.Stop();
    }

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

    private sealed class FailingJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private int _stopRequests;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopRequests => Volatile.Read(ref _stopRequests);

        public void StopApplication() => Interlocked.Increment(ref _stopRequests);
    }
}
