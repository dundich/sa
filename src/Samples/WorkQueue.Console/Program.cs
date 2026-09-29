using Sa.Utils.WorkQueue;

Console.WriteLine("=== Sa.WorkQueue sample ===");

// A simple processor that prints the item, simulates work and honours cancellation.
var processor = new PrintWork(Environment.ProcessorCount);

using var queue = new SaWorkQueue<int>(
    SaWorkQueueOptions<int>
        .Create(processor)
        .WithConcurrencyLimit(3)
        .WithMaxConcurrency(8)
        .WithReaderCancellationOrder(SaReaderCancellationOrder.Lifo)
        .WithStatusCallback((item, status, _) =>
        {
            if (status is SaWorkStatus.Running or SaWorkStatus.Completed)
                Console.WriteLine($"[{processor.Id}] #{item} -> {status}");
        }));

// Enqueue a batch of items.
const int total = 10;

for (var i = 1; i <= total; i++)
    await queue.Enqueue(i, CancellationToken.None);

Console.WriteLine($"Enqueued {total} items, concurrency = {queue.ConcurrencyLimit}");

// While items are flowing, scale concurrency up and back down on the fly.
await Task.Delay(150);
queue.ConcurrencyLimit = 6;
Console.WriteLine($"Scaled up -> concurrency = {queue.ConcurrencyLimit}");

await Task.Delay(150);
queue.ConcurrencyLimit = 2;
Console.WriteLine($"Scaled down -> concurrency = {queue.ConcurrencyLimit}");

await queue.WaitForIdleAsync();

Console.WriteLine($"Processed {processor.Processed} items, queue is idle: {queue.IsIdle()}");

// Graceful shutdown: in-flight items complete, remaining items are reported as Faulted.
await queue.ShutdownAsync();
Console.WriteLine($"Enabled after shutdown: {queue.IsEnabled}");

Console.WriteLine("*** THE END ***");

#pragma warning disable S3903
sealed class PrintWork(int id): ISaWork<int>
#pragma warning restore S3903
{
    private int _processed = 0;

    public int Id => id;
    public int Processed => _processed;

    public async Task Execute(int item, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _processed);
        Console.WriteLine($"[{Id}] #{item} started");
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        Console.WriteLine($"[{Id}] #{item} done");
    }
}
