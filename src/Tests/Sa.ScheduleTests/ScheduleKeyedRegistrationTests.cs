using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Keyed (named) schedules: <c>AddSaSchedule(name, ...)</c> registers a fully independent
/// schedule under that DI key — its own jobs, interceptors, error handlers and options — while
/// the keyless overload keeps registering the default schedule unkeyed (plus a keyed alias
/// under <see cref="Setup.DefaultName"/>).
/// </summary>
public sealed class ScheduleKeyedRegistrationTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    private sealed class JobA : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class JobB : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    [Fact]
    public void AddSaSchedule_Named_ResolvesKeyedSchedulerAndSettings()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("billing", b => b.AddJob<JobA>());
        services.AddSaSchedule(); // the default schedule exists alongside the named one

        using var provider = services.BuildServiceProvider();

        var scheduler = provider.GetRequiredKeyedService<IScheduler>("billing");
        var settings = provider.GetRequiredKeyedService<IScheduleSettings>("billing");

        Assert.Single(scheduler.Jobs);
        Assert.Single(settings.GetJobSettings());

        // Nothing leaked into the default schedule.
        var defaultSettings = provider.GetRequiredService<IScheduleSettings>();
        Assert.Empty(defaultSettings.GetJobSettings());
    }

    [Fact]
    public void TwoNamedSchedules_AreIsolated()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b => b.AddJob<JobA>());
        services.AddSaSchedule("b", b => b.AddJob<JobB>());

        using var provider = services.BuildServiceProvider();

        var settingsA = provider.GetRequiredKeyedService<IScheduleSettings>("a");
        var settingsB = provider.GetRequiredKeyedService<IScheduleSettings>("b");

        Assert.Equal(typeof(JobA), Assert.Single(settingsA.GetJobSettings()).JobType);
        Assert.Equal(typeof(JobB), Assert.Single(settingsB.GetJobSettings()).JobType);
    }

    [Fact]
    public void RepeatedNamedCalls_AccumulateJobsIntoTheSameSchedule()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b => b.AddJob<JobA>());
        services.AddSaSchedule("a", b => b.AddJob<JobB>());

        using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredKeyedService<IScheduleSettings>("a");

        Assert.Equal(2, settings.GetJobSettings().Count());
    }

    [Fact]
    public void SecondConfiguringCall_SameName_Throws_OtherNames_AreFree()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b => b.FromConfiguration("ScheduleA"));

        // A configuring call for a different schedule is fine…
        var differentName = Record.Exception(
            () => services.AddSaSchedule("b", b => b.FromConfiguration("ScheduleB")));
        Assert.Null(differentName);

        Assert.Throws<InvalidOperationException>(
            () => services.AddSaSchedule("a", b => b.FromConfiguration("ScheduleA")));
        Assert.Throws<InvalidOperationException>(
            () => services.AddSaSchedule("a", b => b.Options(o => o.Configure(_ => { }))));

        // …while job-only calls for the same name keep accumulating.
        var jobOnly = Record.Exception(() => services.AddSaSchedule("a", b => b.AddJob<JobA>()));
        Assert.Null(jobOnly);
    }

    [Fact]
    public void DefaultNameAlias_ResolvesTheDefaultSchedule()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b => b.AddJob<JobA>());

        using var provider = services.BuildServiceProvider();

        var defaultScheduler = provider.GetRequiredService<IScheduler>();
        var keyedAlias = provider.GetRequiredKeyedService<IScheduler>(Setup.DefaultName);
        var keyedSettings = provider.GetRequiredKeyedService<IScheduleSettings>(Setup.DefaultName);

        Assert.Same(defaultScheduler, keyedAlias);
        Assert.Single(keyedAlias.Jobs);
        Assert.Single(keyedSettings.GetJobSettings());
    }

    [Fact]
    public void AddSaSchedule_WithDefaultName_RegistersTheDefaultSchedule()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(Setup.DefaultName, b => b.AddJob<JobA>());

        using var provider = services.BuildServiceProvider();

        var defaultScheduler = provider.GetRequiredService<IScheduler>();
        var keyedAlias = provider.GetRequiredKeyedService<IScheduler>(Setup.DefaultName);

        Assert.Same(defaultScheduler, keyedAlias);
        Assert.Single(defaultScheduler.Jobs);
    }

    [Fact]
    public void NamedSchedules_KeepTheirOwnErrorHandlers()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b => b.AddErrorHandler((_, _) => true));
        services.AddSaSchedule("b", b => b.AddJob<JobA>());

        using var provider = services.BuildServiceProvider();

        var settingsA = provider.GetRequiredKeyedService<IScheduleSettings>("a");
        var settingsB = provider.GetRequiredKeyedService<IScheduleSettings>("b");

        Assert.NotNull(settingsA.HandleError);
        Assert.True(settingsA.HandleError!(null!, new InvalidOperationException()));
        Assert.Null(settingsB.HandleError);
    }

    [Fact]
    public void NamedSchedules_KeepTheirOwnInterceptors()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b =>
        {
            b.AddInterceptor<RecordingInterceptor>("first");
            b.AddJob<JobA>();
        });
        services.AddSaSchedule("b", b => b.AddJob<JobA>());

        using var provider = services.BuildServiceProvider();

        var interceptorsA = provider.GetRequiredKeyedService<IInterceptorSettings>("a");
        var interceptorsB = provider.GetRequiredKeyedService<IInterceptorSettings>("b");

        Assert.Single(interceptorsA.Interceptors);
        Assert.Empty(interceptorsB.Interceptors);
    }

    [Fact]
    public void NamedSchedule_UseHostedService_RegistersOneHostPerSchedule()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule("a", b => b.UseHostedService().AddJob<JobA>());
        services.AddSaSchedule("a", b => b.UseHostedService()); // idempotent per name
        services.AddSaSchedule("b", b => b.UseHostedService().AddJob<JobB>());

        using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>();

        // One ScheduleHost per named schedule, not per call.
        Assert.Equal(2, hosted.Count());

        Assert.True(provider.GetRequiredKeyedService<IScheduleSettings>("a").IsHostedService);
        Assert.True(provider.GetRequiredKeyedService<IScheduleSettings>("b").IsHostedService);
    }

    [Fact]
    public void NamedSchedule_OptionsSection_IsIsolated()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ScheduleA:Jobs:JA:Every"] = "00:00:30",
                ["ScheduleB:Jobs:JB:Every"] = "00:00:05",
            }).Build());

        services.AddSaSchedule("a", b =>
        {
            b.AddJob<JobA>().WithName("JA").EveryTime(TimeSpan.FromSeconds(10));
            b.FromConfiguration("ScheduleA");
        });

        services.AddSaSchedule("b", b =>
        {
            b.AddJob<JobB>().WithName("JB").EveryTime(TimeSpan.FromSeconds(60));
            b.FromConfiguration("ScheduleB");
        });

        using var provider = services.BuildServiceProvider();

        var settingsA = provider.GetRequiredKeyedService<IScheduleSettings>("a");
        var settingsB = provider.GetRequiredKeyedService<IScheduleSettings>("b");

        var jobA = settingsA.GetJobSettings().Single();
        var jobB = settingsB.GetJobSettings().Single();

        // Each schedule applied only its own section: A got 30s, B got 5s — not swapped.
        Assert.Equal(From.AddSeconds(30), jobA.Properties.Timing!.GetNextOccurrence(From));
        Assert.Equal(From.AddSeconds(5), jobB.Properties.Timing!.GetNextOccurrence(From));
    }

    [Fact]
    public async Task KeyedSchedule_ErrorAbort_ResolvesItsOwnKeyedHandler()
    {
        // End-to-end: the failing job exhausts retries, and the error handler — which is keyed
        // for a named schedule — must be found through the job's own scope by schedule key,
        // aborting this schedule's job.
        var services = new ServiceCollection();
        var jobId = Guid.NewGuid();

        services.AddSaSchedule("fails", b =>
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

        using var provider = services.BuildServiceProvider();
        var scheduler = provider.GetRequiredKeyedService<IScheduler>("fails");
        var jobScheduler = scheduler.GetSchedule(jobId)!;

        AbortCounter.Clear();

        Assert.Equal(1, await scheduler.Start(TestContext.Current.CancellationToken));
        Assert.True(jobScheduler.IsStarted);

        var deadline = DateTime.UtcNow.AddSeconds(15);

        // Retried once, then the keyed handler aborts the job — with the keyed resolution the
        // job really stops; a null handler (keyed lookup missed) would leave it running forever.
        while (jobScheduler.IsStarted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.False(jobScheduler.IsStarted);
        Assert.True(AbortCounter.Total >= 2);

        await scheduler.Stop();
    }

    [Fact]
    public async Task KeyedStopAllJobs_StopsOnlyItsOwnKeyedScheduler()
    {
        FakeJobScheduler self = new(Guid.NewGuid()) { StopHangs = true };
        FakeJobScheduler other = new(Guid.NewGuid());
        var scheduler = new FakeScheduler(self, other);

        var services = new ServiceCollection();
        services.AddKeyedSingleton<IScheduler>("b", scheduler);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        JobSettings settings = JobSettings.Create<NoopJob>(self.JobId);
        settings.ErrorHandling.ThenStopAllJobs();
        settings.ScheduleKey = "b";

        var context = new JobContext(settings)
        {
            ServiceProvider = scope.ServiceProvider
        };

        // Registered per-schedule: this keyed handler must find the keyed scheduler from the
        // job's own scope and stop only that schedule's other jobs.
        var handler = new JobErrorHandler(new FakeScheduleSettings(), null, null, scheduleKey: "b");

        Task run = Task.Run(() =>
            handler.HandleError(context, new InvalidOperationException("boom")),
            TestContext.Current.CancellationToken);

        Task finished = await Task.WhenAny(run,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.True(ReferenceEquals(run, finished), "HandleError did not return within the guard delay.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("boom", ex.Message);

        Assert.Equal(1, other.StopCalls);
        Assert.Equal(0, self.StopCalls);
    }

    [Fact]
    public void AddSaSchedule_EmptyOrWhitespaceName_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddSaSchedule("", b => b.AddJob<JobA>()));
        Assert.Throws<ArgumentException>(() => services.AddSaSchedule("   ", b => b.AddJob<JobA>()));
    }

    private sealed class RecordingInterceptor : IJobInterceptor
    {
        public Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken cancellationToken)
            => next();
    }

    private sealed class NoopJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class AbortCounter
    {
        private static int _count;

        public static int Total => Volatile.Read(ref _count);

        public static void Inc() => Interlocked.Increment(ref _count);

        public static void Clear() => Interlocked.Exchange(ref _count, 0);
    }

    private sealed class FailingJob : IJob
    {
        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            AbortCounter.Inc();
            throw new InvalidOperationException("always fails");
        }
    }

    private sealed class FakeScheduleSettings : IScheduleSettings
    {
        public bool IsHostedService => false;

        public Func<IJobContext, Exception, bool>? HandleError => null;

        public IEnumerable<IJobSettings> GetJobSettings() => [];
    }

    private sealed class FakeScheduler(params IJobScheduler[] jobs) : IScheduler
    {
        public IScheduleSettings Settings { get; } = new FakeScheduleSettings();

        public IReadOnlyCollection<IJobScheduler> Jobs { get; } = jobs;

        public Task<int> Start(CancellationToken cancellationToken) => Task.FromResult(jobs.Length);

        public Task<int> Restart(CancellationToken cancellationToken) => Task.FromResult(jobs.Length);

        public Task Stop() => Task.WhenAll(jobs.Select(job => job.Stop()));
    }

    private sealed class FakeJobScheduler(Guid jobId) : IJobScheduler
    {
        private int _stopCalls;

        public Guid JobId { get; } = jobId;

        public bool IsStarted => true;

        public int QueueTasks => 0;

        public int ConcurrencyLimit { get; set; }

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public bool StopHangs { get; set; }

        public IChangeToken StartChangeToken() => new CancellationChangeToken(CancellationToken.None);

        public Task<bool> Start(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task Stop()
        {
            Interlocked.Increment(ref _stopCalls);

            if (StopHangs) return Task.Delay(Timeout.Infinite);

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}