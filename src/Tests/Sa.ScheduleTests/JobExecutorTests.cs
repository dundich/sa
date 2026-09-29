using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;
using Sa.Schedule.Engine;
using Sa.Schedule.Settings;

namespace Sa.ScheduleTests;

/// <summary>
/// Tests for the real <see cref="JobExecutor"/>: DI-scope wiring, keyed-service
/// resolution, interceptor proxy chaining, and scope disposal. Real objects only
/// (see <see cref="TrackingScopeFactory"/>), no mocking framework.
/// </summary>
public sealed class JobExecutorTests
{
    [Fact]
    public void Constructor_ResolvesKeyedJob_FromScope()
    {
        var jobId = Guid.NewGuid();
        var factory = TrackingScopeFactory.Create(services =>
            services.AddKeyedScoped<RecordingJob>(jobId));

        var settings = JobSettings.Create<RecordingJob>(jobId);
        var executor = new JobExecutor(settings, new InterceptorSettings([]), factory);

        // The executor exposes the scope's service provider.
        Assert.NotNull(executor.ServiceProvider);

        executor.Dispose();
    }

    [Fact]
    public async Task Execute_DelegatesToRegisteredJob()
    {
        RecordingJob.LastExecute = false;

        var jobId = Guid.NewGuid();
        var factory = TrackingScopeFactory.Create(services =>
            services.AddKeyedScoped<RecordingJob>(jobId));

        var settings = JobSettings.Create<RecordingJob>(jobId);
        var executor = new JobExecutor(settings, new InterceptorSettings([]), factory);

        var context = new JobContext(settings);
        await executor.Execute(context, TestContext.Current.CancellationToken);

        Assert.True(RecordingJob.LastExecute);

        executor.Dispose();
    }

    [Fact]
    public async Task Execute_WrapsWithSingleInterceptor_EnterExitPaired()
    {
        var interceptor = new RecordingInterceptor();

        var jobId = Guid.NewGuid();
        var factory = TrackingScopeFactory.Create(services =>
        {
            services.AddKeyedScoped<RecordingJob>(jobId);
            services.AddKeyedScoped<RecordingInterceptor>(null, (_, _) => interceptor);
        });

        var settings = JobSettings.Create<RecordingJob>(jobId);
        var interceptorSettings = new InterceptorSettings(
            [new JobInterceptorSettings(typeof(RecordingInterceptor), null)]);

        var executor = new JobExecutor(settings, interceptorSettings, factory);

        var context = new JobContext(settings);
        await executor.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(1, interceptor.Entered);
        Assert.Equal(1, interceptor.Exit);

        executor.Dispose();
    }

    [Fact]
    public async Task Execute_InterceptorsAppliedInReverse_OutermostFirst()
    {
        var first = new OrderedInterceptor("first");
        var second = new OrderedInterceptor("second");

        var jobId = Guid.NewGuid();
        var factory = TrackingScopeFactory.Create(services =>
        {
            services.AddKeyedScoped<RecordingJob>(jobId);
            // Registered first -> outermost wrapper.
            services.AddKeyedScoped<OrderedInterceptor>("first", (_, _) => first);
            services.AddKeyedScoped<OrderedInterceptor>("second", (_, _) => second);
        });

        var settings = JobSettings.Create<RecordingJob>(jobId);
        var interceptorSettings = new InterceptorSettings(
            [
                new JobInterceptorSettings(typeof(OrderedInterceptor), "first"),
                new JobInterceptorSettings(typeof(OrderedInterceptor), "second"),
            ]);

        Recorder.Clear();

        var executor = new JobExecutor(settings, interceptorSettings, factory);

        var context = new JobContext(settings);
        await executor.Execute(context, TestContext.Current.CancellationToken);

        // Each interceptor enters/exits exactly once per Execute; "first" is the
        // outermost wrapper, "second" the innermost.
        Assert.Equal(1, first.Entered);
        Assert.Equal(1, first.Exit);
        Assert.Equal(1, second.Entered);
        Assert.Equal(1, second.Exit);

        // The five events must all be present: both "in" records before the job,
        // both "out" records after. Ordering of the async "out" continuations
        // relative to each other is not guaranteed, so assert as a multiset.
        Assert.Equal(
            new[] { "first-in", "second-in", "job", "first-out", "second-out" }.OrderBy(x => x),
            Recorder.Entries.OrderBy(x => x));

        // Both "in" events precede the job, which precedes both "out" events.
        var entries = Recorder.Entries.ToList();
        int lastIn = entries.FindLastIndex(e => e.EndsWith("-in"));
        int jobIdx = entries.IndexOf("job");
        int firstOut = entries.FindIndex(e => e.EndsWith("-out"));
        Assert.True(lastIn < jobIdx && jobIdx < firstOut,
            $"expected 'in' before 'job' before 'out', got [{string.Join(", ", entries)}]");

        executor.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsWhenKeyedJobMissing()
    {
        var factory = TrackingScopeFactory.Create(); // nothing registered

        var jobId = Guid.NewGuid();
        var settings = JobSettings.Create<RecordingJob>(jobId);

        Assert.Throws<InvalidOperationException>(
            () => new JobExecutor(settings, new InterceptorSettings([]), factory));
    }

    sealed class RecordingJob : IJob
    {
        public static bool LastExecute { get; set; }

        public Task Execute(IJobContext context, CancellationToken cancellationToken)
        {
            LastExecute = true;
            Recorder.Add("job");
            return Task.CompletedTask;
        }
    }

    sealed class RecordingInterceptor : IJobInterceptor
    {
        public int Entered { get; private set; }
        public int Exit { get; private set; }

        public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken ct)
        {
            Entered++;
            await next();
            Exit++;
        }
    }

    sealed class OrderedInterceptor(string name) : IJobInterceptor
    {
        private readonly string _name = name;

        public int Entered { get; private set; }
        public int Exit { get; private set; }

        public async Task OnHandle(IJobContext context, Func<Task> next, object? key, CancellationToken ct)
        {
            Entered++;
            Recorder.Add($"{_name}-in");
            await next();
            Exit++;
            Recorder.Add($"{_name}-out");
        }
    }

    /// <summary>
    /// Process-wide capture of the interceptor entry/exit sequence.
    /// </summary>
    sealed class Recorder
    {
        private static readonly Lock sync = new();
        private static readonly Queue<string> _entries = [];

        public static void Add(string entry)
        {
            lock (sync) _entries.Enqueue(entry);
        }

        public static void Clear()
        {
            lock (sync) _entries.Clear();
        }

        public static IReadOnlyList<string> Entries
        {
            get
            {
                lock (sync) return [.. _entries];
            }
        }
    }
}
