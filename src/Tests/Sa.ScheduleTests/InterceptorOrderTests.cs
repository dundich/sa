using Microsoft.Extensions.DependencyInjection;
using Sa.Schedule;

namespace Sa.ScheduleTests;

/// <summary>
/// The first registered interceptor must be the outermost wrapper:
/// it runs first before the job and last after the job.
/// </summary>
public sealed class InterceptorOrderTests : IAsyncDisposable
{
    static class Order
    {
        private static readonly Lock sync = new();
        private static readonly Queue<string> _entries = [];

        public static List<string> Entries
        {
            get
            {
                lock (sync) return [.. _entries];
            }
        }

        public static void Add(string entry)
        {
            lock (sync) _entries.Enqueue(entry);
        }

        public static void Clear()
        {
            lock (sync) _entries.Clear();
        }
    }

    sealed class OuterInterceptor : IJobInterceptor
    {
        public async Task OnHandleAsync(IJobContext context, Func<Task> continuation, object? key, CancellationToken cancellationToken)
        {
            Order.Add("outer-before");
            await continuation();
            Order.Add("outer-after");
        }
    }

    sealed class InnerInterceptor : IJobInterceptor
    {
        public async Task OnHandleAsync(IJobContext context, Func<Task> continuation, object? key, CancellationToken cancellationToken)
        {
            Order.Add("inner-before");
            await continuation();
            Order.Add("inner-after");
        }
    }

    sealed class SomeJob : IJob
    {
        public async Task ExecuteAsync(IJobContext context, CancellationToken cancellationToken)
        {
            Order.Add("job");
            await Task.Delay(10, cancellationToken);
        }
    }

    private readonly ServiceProvider _provider;
    private readonly IScheduler _scheduler;

    public InterceptorOrderTests()
    {
        var services = new ServiceCollection();

        services.AddSaSchedule(b =>
        {
            b
                .AddInterceptor<OuterInterceptor>()   // registered first — outermost
                .AddInterceptor<InnerInterceptor>()
                .AddJob<SomeJob>((sp, job) =>
                {
                    job
                        .EveryTime(TimeSpan.FromMilliseconds(50))
                        .StartImmediate()
                        .RunOnce();
                });
        });

        _provider = services.BuildServiceProvider();
        _scheduler = _provider.GetRequiredService<IScheduler>();
    }

    [Fact]
    public async Task FirstRegisteredInterceptor_IsOutermost()
    {
        Order.Clear();

        Assert.Equal(1, await _scheduler.StartAsync(TestContext.Current.CancellationToken));

        // The job is RunOnce — wait for its single run to finish
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var entries = Order.Entries;
            if (entries.Contains("job") && entries.Contains("outer-after"))
                break;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        string[] expected = ["outer-before", "inner-before", "job", "inner-after", "outer-after"];
        Assert.Equal(expected, Order.Entries);

        await _scheduler.StopAsync();
    }

    public async ValueTask DisposeAsync()
        => await _provider.DisposeAsync();
}
