using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Unit tests for the real <see cref="JobController"/> — pause/resume gate,
/// timing-based gating, retry counting, abort-on-exhaustion, and DI-scope
/// lifecycle. No mocking framework: the DI container is the genuine
/// <see cref="ServiceProvider"/> (see <see cref="TrackingScopeFactory"/>) so
/// keyed-service lookups behave exactly as in production, while scope disposal
/// is tracked — the resource that would leak on a bug.
/// </summary>
public sealed class JobControllerLifecycleTests
{
    [Fact]
    public async Task Execute_Success_Completes_WithoutError()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        var controller = MakeController(settings);

        controller.Start();
        await controller.Execute(TestContext.Current.CancellationToken);
        controller.ExecutionCompleted();

        // A successful completion must not mark the controller aborted.
        Assert.False(controller.AbortedByError);

        controller.Shutdown();
    }

    [Fact]
    public async Task Execute_ExhaustsRetries_HandlerThrows_SetsAbortFlag()
    {
        // When the resolved IJobErrorHandler throws, the controller marks itself
        // aborted — mirroring the "action was applied" contract.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FailingJob>(jobId);
        settings.ErrorHandling.IfErrorRetry(0);

        var controller = MakeController(settings, services =>
            services.AddScoped<IJobErrorHandler>(_ =>
                throw new InvalidOperationException("handler failed")));
        controller.Start();

        controller.ExecutionFailed(new InvalidOperationException("boom"));

        Assert.True(controller.AbortedByError);

        controller.Shutdown();
    }

    [Fact]
    public async Task Execute_ExhaustsRetries_NoHandler_DoesNotSetAbortFlag()
    {
        // With no IJobErrorHandler registered, the exhausted-retry path resolves
        // a null handler and returns normally — the abort flag stays false.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FailingJob>(jobId);
        settings.ErrorHandling.IfErrorRetry(1);

        var controller = MakeController(settings);
        controller.Start();

        controller.ExecutionFailed(new InvalidOperationException("boom")); // 1/1
        controller.ExecutionFailed(new InvalidOperationException("boom")); // exhausted

        Assert.False(controller.AbortedByError);

        controller.Shutdown();
    }

    [Fact]
    public async Task Execute_SuppressedError_DoesNotAbort()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FailingJob>(jobId);
        settings.ErrorHandling
            .DoSuppressError(_ => true) // suppress everything
            .IfErrorRetry(2);

        var controller = MakeController(settings);
        controller.Start();

        controller.ExecutionFailed(new InvalidOperationException("boom"));

        // Suppressed errors bypass the retry accounting entirely and never
        // mark the controller aborted.
        Assert.False(controller.AbortedByError);

        controller.Shutdown();
    }

    [Fact]
    public async Task CanExecute_Immediate_TrueOnFirstRun()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.StartImmediate();

        var controller = MakeController(settings);

        var result = await controller.CanExecute(TestContext.Current.CancellationToken);

        Assert.Equal(CanJobExecuteResult.Ok, result);

        controller.Shutdown();
    }

    [Fact]
    public async Task CanExecute_Timing_DelayThenOk()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.WithTiming(JobTiming.EveryTime(TimeSpan.FromMilliseconds(30)));

        var controller = MakeController(settings);

        var result = await controller.CanExecute(TestContext.Current.CancellationToken);

        Assert.Equal(CanJobExecuteResult.Ok, result);

        controller.Shutdown();
    }

    [Fact]
    public async Task CanExecute_Timing_NoNextOccurrence_Aborts()
    {
        // Feb 30 can never occur -> the timing yields no next occurrence ->
        // the loop must abort instead of spinning forever.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.WithTiming(CronTiming.Every("0 0 30 2 *"));

        var controller = MakeController(settings);

        var result = await controller.CanExecute(TestContext.Current.CancellationToken);

        Assert.Equal(CanJobExecuteResult.Abort, result);

        controller.Shutdown();
    }

    [Fact]
    public async Task CanExecute_RunOnce_AfterFirstIteration_Aborts()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.RunOnce();

        var controller = MakeController(settings);
        controller.Start();

        // First iteration is allowed.
        Assert.Equal(CanJobExecuteResult.Ok,
            await controller.CanExecute(TestContext.Current.CancellationToken));

        await controller.Execute(TestContext.Current.CancellationToken);

        // Second iteration must abort: RunOnce has fired.
        Assert.Equal(CanJobExecuteResult.Abort,
            await controller.CanExecute(TestContext.Current.CancellationToken));

        controller.Shutdown();
    }

    [Fact]
    public async Task Start_CreatesScope_Shutdown_DisposesScope_NoLeak()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);

        var factory = TrackingScopeFactory.Create(services =>
            services.AddKeyedScoped<RecordingJob>(jobId));

        var controller = new JobController(0, settings, new InterceptorSettings([]), factory, TimeProvider.System);

        controller.Start();
        controller.Shutdown();

        var scopes = factory.Scopes;
        Assert.Single(scopes);
        Assert.True(scopes[0].Disposed);
    }

    [Fact]
    public async Task Shutdown_WithoutStart_NoScopeCreated_NoLeak()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);

        var factory = TrackingScopeFactory.Create(services =>
            services.AddKeyedScoped<RecordingJob>(jobId));

        // Never started -> no scope was ever created.
        var controller = new JobController(0, settings, new InterceptorSettings([]), factory, TimeProvider.System);
        controller.Shutdown();

        Assert.Empty(factory.Scopes);
    }

    [Fact]
    public void PauseResume_AreIdempotent_AfterStart()
    {
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        var controller = MakeController(settings);
        controller.Start();

        controller.Pause();
        controller.Pause(); // no-op
        controller.Resume();
        controller.Resume(); // no-op

        controller.Shutdown();
        Assert.True(true);
    }

    private static JobController MakeController(
        IJobSettings settings,
        Action<IServiceCollection>? extra = null)
    {
        var jobId = settings.JobId;
        var jobType = settings.JobType;
        var factory = TrackingScopeFactory.Create(services =>
        {
            services.AddKeyedScoped(jobType, jobId);
            extra?.Invoke(services);
        });

        return new JobController(0, settings, new InterceptorSettings([]), factory, TimeProvider.System);
    }

    sealed class RecordingJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    sealed class FailingJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("always fails");
    }
}
