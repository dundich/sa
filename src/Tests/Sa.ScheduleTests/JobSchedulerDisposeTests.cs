using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Dispose regression for #7: the DI scopes of running iterations must not be
/// disposed from underneath those iterations. Dispose/DisposeAsync cancels the
/// stopping token, gives in-flight iterations a bounded chance (the shutdown
/// timeout) to unwind on their own, and only then force-disposes the remaining
/// controller scopes — so a cooperative iteration always finishes inside a live
/// scope.
/// </summary>
public sealed class JobSchedulerDisposeTests
{
    static class Probe
    {
        private static int _begun;
        private static int _finished;

        public static bool Begun => Volatile.Read(ref _begun) == 1;

        public static bool Finished => Volatile.Read(ref _finished) == 1;

        public static void Reset()
        {
            Interlocked.Exchange(ref _begun, 0);
            Interlocked.Exchange(ref _finished, 0);
        }

        public static void Begin() => Interlocked.Exchange(ref _begun, 1);

        // Set only when the iteration touched its DI scope successfully.
        public static void Complete() => Interlocked.Exchange(ref _finished, 1);
    }

    sealed class ProbeService
    {
    }

    /// <summary>
    /// A job that runs a deliberately long, cancellation-unaware step and then
    /// touches a scoped service. If the scope was disposed while the iteration
    /// was still running, the touch throws and <see cref="Probe"/> is never
    /// completed.
    /// </summary>
    sealed class SlowScopeJob : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            Probe.Begin();

            // Deliberately ignores cancellation: the iteration is still running
            // when Dispose is called and must get the shutdown timeout to finish.
            await Task.Delay(200, CancellationToken.None);

            _ = context.ServiceProvider.GetRequiredService<ProbeService>();

            Probe.Complete();
        }
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightIteration_BeforeDisposingScope()
    {
        Probe.Reset();
        var scheduler = CreateScheduler();

        await scheduler.Start(TestContext.Current.CancellationToken);

        // Wait until the iteration is actually running before disposing.
        await WaitForConditionAsync(() => Probe.Begun);

        await scheduler.DisposeAsync();

        // The iteration touched its (still live) DI scope — nothing was disposed
        // underneath it.
        Assert.True(Probe.Finished, "the DI scope was disposed while the iteration was still running");
    }

    [Fact]
    public async Task Dispose_WaitsForInFlightIteration_BeforeDisposingScope()
    {
        Probe.Reset();
        var scheduler = CreateScheduler();

        await scheduler.Start(TestContext.Current.CancellationToken);

        await WaitForConditionAsync(() => Probe.Begun);

        // The synchronous Dispose path must drain just like DisposeAsync —
        // a cooperative in-flight iteration must not have its scope disposed
        // from underneath it.
        scheduler.Dispose();

        Assert.True(Probe.Finished, "the DI scope was disposed while the iteration was still running");
    }

    private static JobScheduler CreateScheduler()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<SlowScopeJob>(jobId);
        settings.Properties
            .RunOnce()
            .StartImmediate()
            .WithShutdownTimeout(TimeSpan.FromSeconds(2));

        var factory = TrackingScopeFactory.Create(services =>
        {
            services.AddKeyedScoped<SlowScopeJob>(jobId);
            services.AddScoped<ProbeService>();
        });

        return new JobScheduler(
            settings,
            new JobRunner(),
            i => new JobController(i, settings, new InterceptorSettings([]), factory, TimeProvider.System));
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
}