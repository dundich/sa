using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Sa.Utils.WorkQueue;

namespace Sa.Utils.WorkQueue.Benchmarks;

/// <summary>
/// A small, dependency-free harness for the queue's hot paths.
/// </summary>
/// <remarks>
/// Allocations are read with <see cref="GC.GetTotalAllocatedBytes(bool)"/> because
/// reader loops run on thread-pool threads: per-thread accounting would miss nearly
/// all of the queue's own work. Every scenario is warmed up once and then measured
/// over several batches; the reported B/op is process-wide allocation per item,
/// which is the number that moves Gen0 traffic.
/// </remarks>
internal static class Program
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static async Task Main()
    {
        Console.WriteLine($"Runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"GC      : server={GCSettings.IsServerGC}, latency={GCSettings.LatencyMode}");
        Console.WriteLine($"Cores   : {Environment.ProcessorCount}");
        Console.WriteLine();

        Console.WriteLine($"{"scenario",-34} {"ops",12} {"ms",10} {"ops/s",14} {"B/op",12}");
        Console.WriteLine(new string('-', 86));

        var reference = new RefPayload(1);
        var value = new StructPayload(1);

        await RunEnqueue("enqueue/sync/ref c1", reference, new SyncProcessor<RefPayload>(),
            opsPerBatch: 100_000, batches: 5, concurrency: 1);
        await RunEnqueue("enqueue/sync/ref c4", reference, new SyncProcessor<RefPayload>(),
            opsPerBatch: 100_000, batches: 5, concurrency: 4);
        await RunEnqueue("enqueue/await/ref c1", reference, new YieldingProcessor<RefPayload>(),
            opsPerBatch: 20_000, batches: 5, concurrency: 1);
        await RunEnqueue("enqueue/sync/struct c1", value, new SyncProcessor<StructPayload>(),
            opsPerBatch: 100_000, batches: 5, concurrency: 1);
        await RunEnqueue("enqueue/sync/ref+token c1", reference, new SyncProcessor<RefPayload>(),
            opsPerBatch: 50_000, batches: 5, concurrency: 1, callerToken: true);
        await RunEnqueue("enqueue/sync/ref+token detached c1", reference, new SyncProcessor<RefPayload>(),
            opsPerBatch: 50_000, batches: 5, concurrency: 1, callerToken: true, detached: true);
        await RunEnqueue("enqueue/sync/ref+status c1", reference, new SyncProcessor<RefPayload>(),
            opsPerBatch: 100_000, batches: 5, concurrency: 1, observe: true);
        await RunEnqueueConcurrent("enqueue/sync/ref p4c1", reference, new SyncProcessor<RefPayload>(),
            producers: 4, opsPerProducer: 50_000, batches: 5, concurrency: 1);
        await RunEnqueueConcurrent("enqueue/sync/ref p4c4", reference, new SyncProcessor<RefPayload>(),
            producers: 4, opsPerProducer: 50_000, batches: 5, concurrency: 4);

        await RunDrain("drain/no-observer", observe: false, items: 20_000, batches: 5);
        await RunDrain("drain/observer", observe: true, items: 20_000, batches: 5);
    }

    private static async Task RunEnqueue<T>(
        string name,
        T payload,
        ISaWork<T> processor,
        int opsPerBatch,
        int batches,
        int concurrency,
        bool observe = false,
        bool callerToken = false,
        bool detached = false)
    {
        using var callerCts = callerToken ? new CancellationTokenSource() : null;
        var token = callerCts?.Token ?? CancellationToken.None;

        var options = SaWorkQueueOptions<T>.Create(processor)
            .WithQueueCapacity(1024)
            .WithMaxConcurrency(concurrency)
            .WithConcurrencyLimit(concurrency)
            .WithShutdownTimeout(TimeSpan.FromSeconds(30));

        if (observe)
        {
            options = options.WithStatusCallback(static (_, _, _) => { });
        }

        if (detached)
        {
            options = options.WithProducerCancellation(SaProducerCancellationMode.Detached);
        }

        using var queue = new SaWorkQueue<T>(options);

        // Warm up JIT, reader loops and the channel's slow paths. The result is not
        // measured; only steady state is.
        await RunBatch(queue, payload, opsPerBatch, token);

        Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < batches; i++)
        {
            await RunBatch(queue, payload, opsPerBatch, token);
        }

        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        Report(name, (long)opsPerBatch * batches, stopwatch.Elapsed, allocated);
    }

    private static async Task RunBatch<T>(SaWorkQueue<T> queue, T payload, int count, CancellationToken token)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.Enqueue(payload, token);
        }

        await queue.WaitForIdleAsync();
    }

    /// <summary>
    /// Several producers pushing at once. This is the shape where a global lock on
    /// the pending count would serialize, so it is the only scenario that can tell
    /// a lock-free counter from a locked one.
    /// </summary>
    private static async Task RunEnqueueConcurrent<T>(
        string name,
        T payload,
        ISaWork<T> processor,
        int producers,
        int opsPerProducer,
        int batches,
        int concurrency)
    {
        var options = SaWorkQueueOptions<T>.Create(processor)
            .WithQueueCapacity(1024)
            .WithMaxConcurrency(concurrency)
            .WithConcurrencyLimit(concurrency)
            .WithShutdownTimeout(TimeSpan.FromSeconds(30));

        using var queue = new SaWorkQueue<T>(options);

        var producersArray = Enumerable.Range(0, producers)
            .Select(_ => Produce(queue, payload, opsPerProducer))
            .ToArray();

        await Task.WhenAll(producersArray);
        await queue.WaitForIdleAsync();

        Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < batches; i++)
        {
            producersArray = Enumerable.Range(0, producers)
                .Select(_ => Produce(queue, payload, opsPerProducer))
                .ToArray();

            await Task.WhenAll(producersArray);
            await queue.WaitForIdleAsync();
        }

        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        Report(name, (long)opsPerProducer * producers * batches, stopwatch.Elapsed, allocated);
    }

    private static async Task Produce<T>(SaWorkQueue<T> queue, T payload, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await queue.Enqueue(payload, CancellationToken.None);
        }
    }

    private static async Task RunDrain(string name, bool observe, int items, int batches)
    {
        // Warm up the shutdown/drain path once.
        await CreateAndDrain(observe, items);
        Collect();

        var allocated = 0L;
        var elapsed = TimeSpan.Zero;

        for (var i = 0; i < batches; i++)
        {
            using var queue = await CreatePausedQueue(observe, items);

            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            await queue.ShutdownAsync();

            stopwatch.Stop();
            allocated += GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            elapsed += stopwatch.Elapsed;
        }

        Report(name, (long)items * batches, elapsed, allocated);
    }

    private static async Task CreateAndDrain(bool observe, int items)
    {
        using var queue = await CreatePausedQueue(observe, items);
        await queue.ShutdownAsync();
    }

    /// <summary>
    /// A paused queue with a full buffer: no reader exists, so shutdown can only
    /// drop what is inside it. That isolates <c>DrainAndResetIdle</c> from any
    /// processing work.
    /// </summary>
    private static async Task<SaWorkQueue<int>> CreatePausedQueue(bool observe, int items)
    {
        var options = SaWorkQueueOptions<int>.Create(static (_, _) => Task.CompletedTask)
            .WithConcurrencyLimit(0)
            .WithQueueCapacity(items)
            .WithEnqueueStrategy(SaEnqueueStrategy.Skip);

        if (observe)
        {
            options = options.WithStatusCallback(static (_, _, _) => { });
        }

        var queue = new SaWorkQueue<int>(options);

        for (var i = 0; i < items; i++)
        {
            await queue.Enqueue(i, CancellationToken.None);
        }

        return queue;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Report(string name, long operations, TimeSpan elapsed, long allocated)
    {
        var ms = elapsed.TotalMilliseconds;
        var opsPerSecond = operations / elapsed.TotalSeconds;
        var bytesPerOp = (double)allocated / operations;

        Console.WriteLine(
            $"{name,-34} {operations.ToString("N0", Culture),12} {ms.ToString("N1", Culture),10} " +
            $"{opsPerSecond.ToString("N0", Culture),14} {bytesPerOp.ToString("N1", Culture),12}");
    }

    private sealed record RefPayload(int Id);

    private readonly record struct StructPayload(int Id);

    private sealed class SyncProcessor<T> : ISaWork<T>
    {
        public Task Execute(T input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Forces the reader to suspend, so the per-item async state machines are
    /// actually allocated. A synchronous processor hides that cost.
    /// </summary>
    private sealed class YieldingProcessor<T> : ISaWork<T>
    {
        public async Task Execute(T input, CancellationToken cancellationToken)
            => await Task.Yield();
    }
}
