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
        settings.Properties.WithCron("0 0 30 2 *");

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

    [Fact]
    public async Task CanExecute_DelayBeyondTaskDelayCeiling_DoesNotFault_IsCancellable()
    {
        // Regression (#1): Task.Delay rejects a single delay above int.MaxValue ms
        // (~24.9 days) with ArgumentOutOfRangeException. The wait must be chunked
        // instead — otherwise the exception escapes CanExecute (outside the runner's
        // per-iteration try/catch), faults the queue item and silently kills the slot
        // while IsStarted still reports true.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.EveryTime(TimeSpan.FromDays(60));

        var controller = MakeController(settings);
        using var cts = new CancellationTokenSource();

        Task<CanJobExecuteResult> run = controller.CanExecute(cts.Token).AsTask();

        // Give a (synchronously) faulting implementation a chance to surface.
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(run.IsFaulted, run.Exception?.ToString());
        Assert.False(run.IsCompleted);

        // The chunked wait must still honour cancellation.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        controller.Shutdown();
    }

    [Fact]
    public async Task WaitToRun_InitialDelayBeyondTaskDelayCeiling_DoesNotFault_IsCancellable()
    {
        // Same regression for the second Task.Delay call site: a long InitialDelay
        // (e.g. OnceIn/WithInitialDelay of 60 days) must not fault WaitToRun —
        // a fault there skips controller.Start() and the slot dies silently.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);
        settings.Properties.WithInitialDelay(TimeSpan.FromDays(60));

        var controller = MakeController(settings);
        using var cts = new CancellationTokenSource();

        Task run = controller.WaitToRun(cts.Token).AsTask();

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(run.IsFaulted, run.Exception?.ToString());
        Assert.False(run.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        controller.Shutdown();
    }

    [Fact]
    public async Task RunOnce_RetryOwed_KeepsGateOpen_BudgetSpent_Aborts()
    {
        // Regression (#4): the RunOnce gate used to abort as soon as
        // NumIterations > 0 — before the retry policy could ever run, so
        // IfErrorRetry/ThenXxx were dead code for a RunOnce job.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FailingJob>(jobId);
        settings.Properties.RunOnce();
        settings.ErrorHandling.IfErrorRetry(1);

        var controller = MakeController(settings);
        controller.Start();
        var ct = TestContext.Current.CancellationToken;

        // First attempt is allowed.
        Assert.Equal(CanJobExecuteResult.Ok, await controller.CanExecute(ct));

        // It fails, and one retry is still owed.
        var first = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.Execute(ct));
        controller.ExecutionFailed(first);
        Assert.False(controller.AbortedByError);

        // The gate must stay open between two attempts of the *same* run.
        Assert.Equal(CanJobExecuteResult.Ok, await controller.CanExecute(ct));

        // The second attempt fails too; the budget (1 retry) is now spent and
        // nothing is owed — the job has run as "once" as it ever will.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.Execute(ct));
        controller.ExecutionFailed(second);

        Assert.Equal(CanJobExecuteResult.Abort, await controller.CanExecute(ct));

        controller.Shutdown();
    }

    [Fact]
    public async Task RunOnce_RetrySucceeded_ClearsRetryDebt_GateCloses()
    {
        // After a consumed retry succeeds, ExecutionCompleted clears the retry
        // debt and the RunOnce gate must close again — the job must not loop.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FlakyJob>(jobId);
        settings.Properties.RunOnce();
        settings.ErrorHandling.IfErrorRetry(1);

        int attempts = 0;

        var controller = MakeController(settings, services =>
            services.AddKeyedScoped<FlakyJob>(jobId, (_, _) =>
                new FlakyJob(() => Interlocked.Increment(ref attempts) == 1)));

        controller.Start();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(CanJobExecuteResult.Ok, await controller.CanExecute(ct));

        // Attempt 1 fails (retry owed), attempt 2 succeeds.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.Execute(ct));
        controller.ExecutionFailed(failure);

        await controller.Execute(ct);
        controller.ExecutionCompleted();

        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(CanJobExecuteResult.Abort, await controller.CanExecute(ct));

        controller.Shutdown();
    }

    [Fact]
    public async Task Runner_RunOnceJob_ConsumesRetryBudget_AbortsAfterExhaustion()
    {
        // End-to-end over the real loop: a failing RunOnce job must get its
        // whole retry budget (1 + 2 retries) before the loop aborts, instead
        // of aborting after the very first failure.
        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<FlakyJob>(jobId);
        settings.Properties.RunOnce();
        settings.ErrorHandling.IfErrorRetry(2);

        int attempts = 0;

        var factory = TrackingScopeFactory.Create(services =>
            services.AddKeyedScoped<FlakyJob>(jobId, (_, _) => new FlakyJob(() =>
            {
                Interlocked.Increment(ref attempts);
                return true;
            })));

        var controller = new JobController(
            0, settings, new InterceptorSettings([]), factory, TimeProvider.System);

        bool aborted = await new JobRunner()
            .Run(controller, TestContext.Current.CancellationToken);

        Assert.Equal(3, Volatile.Read(ref attempts)); // 1 attempt + 2 retries

        // The loop ended because a RunOnce job has no more work — from the
        // scheduler's point of view this is a finished job (it must stop and
        // tear its readers down), so Run reports true. No error handler threw,
        // so the controller itself is not marked AbortedByError.
        Assert.True(aborted);
        Assert.False(controller.AbortedByError);
    }

    private static JobController MakeController(
        JobSettings settings,
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

    /// <summary>
    /// Fails when <paramref name="shouldFail"/> says so — lets a test script
    /// fail-then-succeed sequences (retry debt, RunOnce gate).
    /// </summary>
    sealed class FlakyJob(Func<bool> shouldFail) : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => shouldFail()
                ? throw new InvalidOperationException("flaky")
                : Task.CompletedTask;
    }
}
