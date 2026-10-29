using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Sa.Outbox;
using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;
using Sa.Outbox.Publication;

namespace Sa.Outbox.Benchmarks;

/// <summary>
/// Dependency-free measurements for the Outbox publish and delivery envelopes.
/// </summary>
/// <remarks>
/// This is deliberately not a database benchmark: it isolates the object graph the queue
/// allocates per message, which is what the struct-vs-class decision turns on. The bulk writer
/// and metadata provider are fakes, so wall time is mostly loop overhead; B/op is the number
/// that matters here.
/// </remarks>
internal static class Program
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task Main()
    {
        Console.WriteLine($"Runtime : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"GC      : server={GCSettings.IsServerGC}, latency={GCSettings.LatencyMode}");
        Console.WriteLine($"Cores   : {Environment.ProcessorCount}");
        Console.WriteLine();

        Console.WriteLine($"{"scenario",-34} {"ops",12} {"ms",10} {"ops/s",14} {"B/op",12}");
        Console.WriteLine(new string('-', 86));

        await RunPublish();
        RunDelivery();
    }

    private static async Task RunPublish()
    {
        var writer = new NullBulkWriter();
        var metadata = new MetadataConfiguration();
        metadata.AddMetadata<RefPayload>("root", static _ => string.Empty);

        var publisher = new OutboxMessagePublisher(
            TimeProvider.System,
            writer,
            new OutboxPublishSettings(MaxBatchSize: 1024),
            metadata);

        var messages = new RefPayload[1024];
        for (var i = 0; i < messages.Length; i++)
        {
            messages[i] = new RefPayload(i);
        }

        // Warm up the array pool and the per-publish path.
        await publisher.Publish(messages, tenantId: 1, context: null, None);
        Collect();

        const int PublishBatches = 1000;

        await MeasureAsync("publish/batch/ref", PublishBatches * messages.Length, async () =>
        {
            for (var i = 0; i < PublishBatches; i++)
            {
                await publisher.Publish(messages, tenantId: 1, context: null, None);
            }
        });

        await MeasureAsync("publish/single/ref", 1_000_000, async () =>
        {
            var payload = new RefPayload(1);
            for (var i = 0; i < 1_000_000; i++)
            {
                await publisher.PublishSingle(payload, tenantId: 1, context: null, None);
            }
        });

        var largeMessages = new LargePayload[1024];
        for (var i = 0; i < largeMessages.Length; i++)
        {
            largeMessages[i] = new LargePayload(i, default);
        }

        await publisher.Publish(largeMessages, tenantId: 1, context: null, None);

        await MeasureAsync("publish/batch/large-struct", PublishBatches * largeMessages.Length, async () =>
        {
            for (var i = 0; i < PublishBatches; i++)
            {
                await publisher.Publish(largeMessages, tenantId: 1, context: null, None);
            }
        });
    }

    private static void RunDelivery()
    {
        var factory = new OutboxContextFactory();
        var part = new OutboxPartInfo(1, "root", DateTimeOffset.UnixEpoch);
        var info = new OutboxTaskDeliveryInfo(
            TaskId: 1,
            DeliveryId: 0,
            Attempt: 0,
            LastErrorId: 0,
            Status: default,
            PartInfo: part);

        var reference = new RefPayload(1);
        var value = new StructPayload(1);
        var large = new LargePayload(1, default);

        // One warm-up context, then the pool serves every iteration.
        CreateAndReturn(factory, reference, part, info);
        Collect();

        MeasureSync("delivery/context/ref", 1_000_000, () =>
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                CreateAndReturn(factory, reference, part, info);
            }
        });

        MeasureSync("delivery/context/struct", 1_000_000, () =>
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                CreateAndReturn(factory, value, part, info);
            }
        });

        MeasureSync("delivery/context/large-struct", 1_000_000, () =>
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                CreateAndReturn(factory, large, part, info);
            }
        });
    }

    private static void CreateAndReturn<TMessage>(
        OutboxContextFactory factory,
        TMessage payload,
        OutboxPartInfo part,
        OutboxTaskDeliveryInfo info)
    {
        var message = new OutboxMessage<TMessage>("id", payload, part);
        var delivery = new OutboxDeliveryMessage<TMessage>(Guid.Empty, message, info);

        IOutboxContextOperations<TMessage> context = factory.Create(delivery);
        context.Ok();
        factory.Return(context);
    }

    private static void MeasureSync(string name, int operations, Action body)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        body();

        stopwatch.Stop();
        Report(name, operations, stopwatch.Elapsed, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    private static async Task MeasureAsync(string name, int operations, Func<Task> body)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        await body();

        stopwatch.Stop();
        Report(name, operations, stopwatch.Elapsed, GC.GetTotalAllocatedBytes(precise: true) - before);
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

    [System.Runtime.CompilerServices.InlineArray(64)]
    private struct LongBuffer
    {
        private long _element0;
    }

    /// <summary>~512-byte value payload: the worst case for a struct envelope.</summary>
    private readonly record struct LargePayload(int Id, LongBuffer Padding);

    /// <summary>Accepts both bulk-writer shapes and allocates nothing itself.</summary>
    private sealed class NullBulkWriter : IOutboxBulkWriter
    {
        public ValueTask<ulong> InsertBulk<TMessage>(
            OutboxMessageBatch<TMessage> batch,
            object? context,
            CancellationToken cancellationToken = default)
            => new((ulong)batch.Count);

        public ValueTask<ulong> InsertBulk<TMessage>(
            ReadOnlyMemory<OutboxMessage<TMessage>> messages,
            CancellationToken cancellationToken = default)
            => new((ulong)messages.Length);
    }
}
