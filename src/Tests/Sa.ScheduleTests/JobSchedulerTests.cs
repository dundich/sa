using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;
using System.Collections.Concurrent;

namespace Sa.ScheduleTests;

public class JobSchedulerTests
{

    [Fact]
    public async Task Start_ConcurrentCalls_OnlyOneSucceeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));
        var results = new ConcurrentBag<bool>();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => results.Add(
                await scheduler.Start(TestContext.Current.CancellationToken))
            ));

        await Task.WhenAll(tasks);

        Assert.Single(results, r => r);
    }


    [Fact]
    public async Task Stop_GivesUpAfterConfiguredShutdownTimeout()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(2)
            .WithConcurrencyLimit(1)
            .WithShutdownTimeout(TimeSpan.FromMilliseconds(300));

        var scheduler = new JobScheduler(settings, new HangingRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);

        // Let the reader take the (hanging) work into flight before we stop.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.Stop();
        sw.Stop();

        // Stop must give up after the configured shutdown timeout (300ms),
        // not wait out the 30s default — and not return instantly either
        // (the work item never completes, so the wait really ran).
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task Stop_DefaultTimeout_BoundedByConfiguredValue()
    {
        // No WithShutdownTimeout configured: the default 30s applies, but the
        // wait is still bounded — a cooperative runner finishing before the
        // timeout lets Stop return immediately, well under the default.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(2)
            .WithConcurrencyLimit(1);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.Stop();
        sw.Stop();

        // The runner completes immediately, so Stop must not wait out the
        // default 30s timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Stop took {sw.Elapsed} with the default timeout");

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task Stop_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
        Task.Run(async () =>
            await scheduler.Stop()
        ));

        await Task.WhenAll(tasks);


        Assert.False(scheduler.IsStarted);
        Assert.Equal(0, scheduler.QueueTasks);
    }


    [Fact]
    public async Task Dispose_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => await scheduler.DisposeAsync()));

        await Task.WhenAll(tasks);

        Assert.False(scheduler.IsStarted);
        Assert.Equal(0, scheduler.QueueTasks);
    }


    [Fact]
    public async Task ConcurrencyLimit_ConcurrentCalls_Succeeds()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(30)
            .WithConcurrencyLimit(1);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);

        Assert.True(scheduler.IsStarted);

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => scheduler.ConcurrencyLimit = Random.Shared.Next(2, 45)));

        await Task.WhenAll(tasks);

        Assert.True(scheduler.IsStarted);
        Assert.InRange(scheduler.ConcurrencyLimit, 2, 30);

        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task IsStarted_True_AfterSuccessfulStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.False(scheduler.IsStarted);

        var started = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(started);
        Assert.True(scheduler.IsStarted);
    }

    [Fact]
    public async Task Start_AfterHungRun_GivesUpAfterShutdownTimeout()
    {
        // Stop only waits out the shutdown timeout, so the previous run's item can
        // still be in flight. Start used to wait for idle without any bound and hung
        // Restart()/Start() forever; it must give up within the same timeout.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.WithShutdownTimeout(TimeSpan.FromMilliseconds(300));

        var scheduler = new JobScheduler(settings, new HangingRunner(), i => new TestJobController(i));

        Assert.True(await scheduler.Start(TestContext.Current.CancellationToken));

        // Let the reader take the hanging item into flight, then stop past the timeout.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await scheduler.Stop();
        Assert.False(scheduler.IsStarted);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // WaitAsync keeps the failure fast on a regression: an unbounded Start would
        // otherwise hang the whole suite instead of failing this one test.
        bool started = await scheduler
            .Start(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        sw.Stop();

        Assert.False(started);
        Assert.False(scheduler.IsStarted);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));

        await scheduler.DisposeAsync();
    }

    [Fact]
    public void QueueTasks_ReturnsZero_BeforeStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.Equal(0, scheduler.QueueTasks);
    }

    [Fact]
    public async Task Start_AlreadyCancelledToken_ReturnsFalse_AndRemainsStartable()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        var cts = new CancellationTokenSource();
        cts.Cancel();

        bool started = await scheduler.Start(cts.Token);

        Assert.False(started);
        Assert.False(scheduler.IsStarted);

        // Regression: a failed start must not leave the scheduler locked in
        // "started" state — a subsequent start with a fresh token succeeds.
        bool restarted = await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(restarted);
        Assert.True(scheduler.IsStarted);

        await scheduler.Stop();
        Assert.False(scheduler.IsStarted);
    }

    [Fact]
    public void Dispose_AfterStop_IsSafe()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        scheduler.Dispose();
        scheduler.Dispose(); // Double dispose should not throw
        Assert.True(true);
    }

    [Fact]
    public async Task DisposeAsync_AfterStop_IsSafe()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.DisposeAsync();
        await scheduler.DisposeAsync(); // Double dispose should not throw
        Assert.True(true);
    }

    [Fact]
    public void ChangeToken_ReflectsStoppedState()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        var token = scheduler.StartChangeToken();
        Assert.NotNull(token);
    }


    [Fact]
    public async Task ChangeToken_AfterStart_WaitsForNextStop()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);

        // While running, the token waits for the next stop and must not be
        // fired prematurely.
        var token = scheduler.StartChangeToken();
        Assert.False(token.HasChanged);

        await scheduler.Stop();

        Assert.True(token.HasChanged);

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task ChangeToken_StoppedJob_HasNoPendingTransition()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        await scheduler.Stop();
        Assert.False(scheduler.IsStarted);

        // Previously this returned the already-cancelled (already-fired) source
        // token, so a subscriber got a phantom "change" for a transition that
        // happened before it subscribed. A stopped job has no pending
        // transition — the token must be inert; IsStarted is the state query.
        var token = scheduler.StartChangeToken();
        Assert.False(token.HasChanged);

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task RunOnce_NaturalCompletion_StopsAndRefusesToRestart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.RunOnce();

        // The runner reports "finished" (the loop ended because there is no
        // more work) — exactly what happens when a real RunOnce job's gate
        // closes after its single iteration.
        var scheduler = new JobScheduler(settings, new FinishedRunner(), i => new TestJobController(i));

        Assert.True(await scheduler.Start(TestContext.Current.CancellationToken));

        // No error happened, yet the job must stop itself — IsStarted must not
        // be left stuck true over a dead job.
        await WaitForConditionAsync(() => !scheduler.IsStarted);
        Assert.False(scheduler.IsStarted);

        // "stop permanently": the RunOnce is consumed, so a later Start is
        // refused instead of silently re-running the job.
        Assert.False(await scheduler.Start(TestContext.Current.CancellationToken));
        Assert.False(scheduler.IsStarted);

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task Stop_DuringStart_StopsTheStart()
    {
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.WithShutdownTimeout(TimeSpan.FromSeconds(2));

        var runner = new OneShotGateRunner();
        var scheduler = new JobScheduler(settings, runner, i => new TestJobController(i));

        // Start #1: the reader picks the item and parks on the gate — the queue
        // is now busy with an item that cannot finish until the gate is released.
        Assert.True(await scheduler.Start(TestContext.Current.CancellationToken));
        await runner.Started;
        Assert.True(scheduler.IsStarted);

        // Stop times out on the parked item (bounded by the shutdown timeout):
        // nothing can drain the queue while the gate is closed. This also proves
        // the queue really is busy before we build on that below.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.Stop();
        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(1500),
            $"Stop returned immediately ({sw.ElapsedMilliseconds}ms) — the queue was not busy");
        Assert.False(scheduler.IsStarted);

        // Start #2 blocks in WaitForIdleAsync while the old item is still in
        // flight — it cannot finish before the gate is released, so by the time
        // Stop() runs below the start is guaranteed to be in flight.
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startTask = scheduler.Start(startCts.Token);
        Assert.False(startTask.IsCompleted,
            "Start #2 must wait for the in-flight item — it was not in flight");

        // Stop lands while Start is still starting. It must not be lost: the
        // in-flight start must see it and cancel itself.
        await scheduler.Stop();

        runner.Release();

        Assert.False(await startTask);
        Assert.False(scheduler.IsStarted);

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task ConcurrencyLimit_WithoutMaxConcurrency_DefaultsMaxToLimit()
    {
        // Documented contract (IJobProperties.MaxConcurrency): an unset
        // MaxConcurrency equals the effective ConcurrencyLimit, so a bare
        // WithConcurrencyLimit(5) allocates 5 slots. Before the fix the queue
        // was silently capped at 1 because MaxConcurrency defaulted to 1.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.WithConcurrencyLimit(5);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        await scheduler.Start(TestContext.Current.CancellationToken);
        Assert.True(scheduler.IsStarted);
        Assert.Equal(5, scheduler.ConcurrencyLimit);

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task RunOnce_FaultedRun_DoesNotConsumeTheRunBudget()
    {
        // "Faulted" is not a natural completion: the RunOnce budget must survive, so
        // the job can be started again after the fault (a broken timing, a failed
        // construction) is fixed.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties.RunOnce();

        var runner = new FaultedRunner();
        var scheduler = new JobScheduler(settings, runner, i => new TestJobController(i));

        // The first start may report true (the fault landed after the slots were
        // spawned) or false (it landed during startup) — neither may consume the run.
        _ = await scheduler.Start(TestContext.Current.CancellationToken);
        await WaitForConditionAsync(() => runner.Attempts >= 1 && !scheduler.IsStarted);

        Assert.False(scheduler.IsStarted);

        // A second start must reach the runner again instead of being refused as a
        // consumed RunOnce — that is what "budget intact" means. Start() may return
        // false while the previous start is still settling, so poll until the runner
        // is actually reached; a consumed RunOnce would never reach it.
        int attemptsBefore = runner.Attempts;

        await WaitForConditionAsync(() =>
        {
            _ = scheduler.Start(TestContext.Current.CancellationToken);
            return runner.Attempts > attemptsBefore;
        });

        await scheduler.DisposeAsync();
    }


    [Fact]
    public async Task CallerTokenCancelledLater_MarksTheJobStopped()
    {
        // The stop token is linked to the caller's token, so cancelling the caller's
        // token (for example the host's startup token) ends the slots without Stop().
        // The scheduler must not keep reporting a running job in that case.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        var scheduler = new JobScheduler(settings, new TokenWaitingRunner(), i => new TestJobController(i));

        using var cts = new CancellationTokenSource();

        Assert.True(await scheduler.Start(cts.Token));
        Assert.True(scheduler.IsStarted);

        await cts.CancelAsync();

        await WaitForConditionAsync(() => !scheduler.IsStarted);

        Assert.False(scheduler.IsStarted);

        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task CallerTokenCancelledBeforeTheSlotRuns_MarksTheJobStopped()
    {
        // The same contract as above, but on the losing side of a startup race: the caller's token
        // can be cancelled after Start() returned and before a reader picked the slot up. The queue
        // then drops the item at its own cancellation gate *without* invoking the processor
        // (SaWorkQueue.ExecuteItemAsync throws before Execute and reports the item as Aborted), so no
        // slot ever observes the cancellation. The scheduler has to reflect the stop from the stop
        // token itself; relying on a running slot left IsStarted true over a dead job forever.
        //
        // Repeated because which side of the race a given iteration lands on is up to the thread
        // pool — a single run could pass on the broken code by starting the slot first.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
            var runner = new CountingTokenWaitingRunner();
            var scheduler = new JobScheduler(settings, runner, i => new TestJobController(i));

            using var cts = new CancellationTokenSource();

            Assert.True(await scheduler.Start(cts.Token));
            await cts.CancelAsync();

            await WaitForConditionAsync(() => !scheduler.IsStarted);

            Assert.True(!scheduler.IsStarted, $"attempt {attempt}: IsStarted stayed true (runner calls: {runner.Calls})");

            await scheduler.DisposeAsync();
        }
    }

    /// <summary>
    /// <see cref="TokenWaitingRunner"/> that counts how many times it was actually entered, so a
    /// failing attempt can say whether the slot ran at all.
    /// </summary>
    class CountingTokenWaitingRunner : IJobRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return JobRunResult.Finished;
            }
            catch (OperationCanceledException)
            {
                return JobRunResult.Stopped;
            }
        }
    }


    [Fact]
    public async Task ReaderPool_IsPausedUntilTheFirstStart()
    {
        // The queue used to be created with MaxConcurrency readers, so resolving a
        // scheduler (even one that is never started) allocated the whole pool. It now
        // starts paused and is raised to MaxConcurrency on Start.
        var settings = JobSettings.Create<TestJob>(Guid.NewGuid());
        settings.Properties
            .WithMaxConcurrency(3)
            .WithConcurrencyLimit(1);

        var scheduler = new JobScheduler(settings, new TestJobRunner(), i => new TestJobController(i));

        Assert.Equal(Sa.Utils.WorkQueue.SaWorkPoolState.Paused, ReaderPoolOf(scheduler).PoolState);

        await scheduler.Start(TestContext.Current.CancellationToken);

        await WaitForConditionAsync(() =>
            ReaderPoolOf(scheduler).PoolState == Sa.Utils.WorkQueue.SaWorkPoolState.Active);
        Assert.Equal(3, ReaderPoolOf(scheduler).MaxConcurrency);

        await scheduler.DisposeAsync();
    }


    private static Sa.Utils.WorkQueue.ISaWorkQueue<IJobController> ReaderPoolOf(JobScheduler scheduler)
    {
        var field = typeof(JobScheduler).GetField(
            "_queue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        return (Sa.Utils.WorkQueue.ISaWorkQueue<IJobController>)field.GetValue(scheduler)!;
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



    class TestJob : IJob
    {
        public async Task Execute(IJobContext context, CancellationToken cancellationToken)
            => await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
    }


    class TestJobRunner : IJobRunner
    {
        public Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
            => Task.FromResult(JobRunResult.Stopped);
    }


    // A work item that never completes and ignores cancellation — the queue
    // can never drain on its own, so Stop/Dispose must be bounded by the
    // configured shutdown timeout.
    class HangingRunner : IJobRunner
    {
        private static readonly Task<JobRunResult> Never =
            new TaskCompletionSource<JobRunResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
            => Never;
    }


    // Reports "finished" immediately — the same result a real runner returns
    // when a RunOnce gate closes or an unsatisfiable schedule ends the loop.
    class FinishedRunner : IJobRunner
    {
        public Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
            => Task.FromResult(JobRunResult.Finished);
    }


    // Reports a loop-level fault — the same result a real runner returns when the
    // job cannot be constructed or a custom timing throws. Counts attempts so a
    // test can tell "started and faulted" from "refused by the RunOnce guard".
    class FaultedRunner : IJobRunner
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(JobRunResult.Faulted);
        }
    }


    // Waits on the stop token and reports a plain stop when it fires — models a
    // job that ends because the caller's own token was cancelled (no Stop() call).
    class TokenWaitingRunner : IJobRunner
    {
        public async Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return JobRunResult.Finished;
            }
            catch (OperationCanceledException)
            {
                return JobRunResult.Stopped;
            }
        }
    }


    // An item that stays in flight (ignoring cancellation) until the gate is
    // released — used to deterministically keep Start #2 waiting in its
    // WaitForIdleAsync while Stop() lands. Started completes only when the
    // reader has actually picked the item and entered RunJob, i.e. when the
    // queue is genuinely busy.
    class OneShotGateRunner : IJobRunner
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Release() => _gate.TrySetResult();

        public async Task<JobRunResult> Run(IJobController controller, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _gate.Task;
            return JobRunResult.Stopped;
        }
    }


    class TestJobController(int index) : IJobController
    {
        public bool IsPaused => false;

        public bool AbortedByError => false;

        public int Index => index;

        public ValueTask<CanJobExecuteResult> CanExecute(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(CanJobExecuteResult.Ok);
        }

        public Task Execute(CancellationToken cancellationToken) => Task.CompletedTask;

        public void ExecutionCompleted()
        {
            //
        }

        public void ExecutionFailed(Exception exception)
        {
            //
        }

        public void Pause()
        {

        }

        public void Resume()
        {

        }

        public void Start()
        {
            //
        }

        public void Shutdown()
        {
            //
        }

        public ValueTask WaitIfPaused(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public ValueTask WaitToRun(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
