using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;

namespace Sa.Utils.WorkQueue.Tests;

/// <summary>
/// Tests for the diagnosis that reaches a caller.
/// </summary>
/// <remarks>
/// A status callback has no logger of its own, so the exception handed to it is the
/// only account of why an item stopped. These tests pin the three places where that
/// account used to go missing: the cancellation statuses, the "queue stopped" error
/// raised to a parked producer, and <see cref="ISaWorkQueue{TInput}.ShutdownError"/>.
/// </remarks>
public sealed class WorkQueueDiagnosticsTests
{
    static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AbortedStatus_ReportsTheOperationCanceledException()
    {
        Exception? reported = null;
        var seen = new List<SaWorkStatus>();
        var inProcessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var producerCts = new CancellationTokenSource();
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken ct) =>
            {
                inProcessor.SetResult();
                await release.Task.ConfigureAwait(false);

                // The processor surfaces the producer's cancellation as it sees it.
                // The queue catches it and reports Aborted (the producer gave up, the
                // reader is fine) — the exception must travel with that status.
                throw new OperationCanceledException("producer gave up", producerCts.Token);
            })
                .WithConcurrencyLimit(1)
                .WithStatusCallback((_, status, error) =>
                {
                    seen.Add(status);
                    if (status == SaWorkStatus.Aborted) reported = error;
                }));

        var enqueued = queue.Enqueue(1, producerCts.Token);

        await inProcessor.Task.WaitAsync(TestToken);
        await producerCts.CancelAsync();
        release.SetResult();

        Assert.True(await enqueued);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.Contains(SaWorkStatus.Aborted, seen);

        // Before the fix the exception was dropped: the callback saw null, and could
        // not tell an abort from any other terminal status.
        var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(reported);
        Assert.Equal(producerCts.Token, cancelled.CancellationToken);
    }

    [Fact]
    public async Task CancelledStatus_ReportsTheOperationCanceledException()
    {
        Exception? reported = null;
        var inProcessor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken ct) =>
            {
                inProcessor.SetResult();

                // The queue, not the producer, ends this work: the token the processor
                // observes is the reader's, so the status is Cancelled and the reader
                // leaves the pool.
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            })
                .WithConcurrencyLimit(1)
                .WithStatusCallback((_, status, error) =>
                {
                    if (status == SaWorkStatus.Cancelled) reported = error;
                }));

        await queue.Enqueue(1, TestToken);

        await inProcessor.Task.WaitAsync(TestToken);
        await queue.ShutdownAsync();

        Assert.IsAssignableFrom<OperationCanceledException>(reported);
    }

    [Fact]
    public async Task ParkedProducer_ReceivesTheCauseWithTheStoppedError()
    {
        // Paused with capacity 1: the second producer parks in the channel's write
        // until shutdown completes the writer.
        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((int input, CancellationToken ct) => Task.CompletedTask)
                .WithConcurrencyLimit(0)
                .WithMaxConcurrency(4)
                .WithQueueCapacity(1));

        await queue.Enqueue(1, TestToken);

        var parked = Task.Run(() => queue.Enqueue(2, TestToken).AsTask(), TestToken);

        await Task.Delay(200, TestToken);
        Assert.False(parked.IsCompleted);

        await queue.ShutdownAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => parked);

        // Before the fix the channel exception that woke the producer was discarded,
        // so "ChannelClosedException" — which says nothing about why the queue
        // stopped — was all the caller could learn.
        Assert.IsType<ChannelClosedException>(ex.InnerException);
    }

    [Fact]
    public async Task ShutdownError_IsTheFaultThatStartedTheShutdown()
    {
        var first = new InvalidOperationException("first failure");

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create((int input, CancellationToken ct) => throw first)
                .WithConcurrencyLimit(1)
                .WithHandleItemFaulted((_, _) => SaExecutionErrorStrategy.ShutdownQueue));

        await queue.Enqueue(1, TestToken);
        await queue.WaitForIdleAsync(cancellationToken: TestToken);

        Assert.False(queue.IsEnabled);
        Assert.Same(first, queue.ShutdownError);
    }

    [Fact]
    public async Task ShutdownError_KeepsTheRootCauseWhenALaterItemFaultsToo()
    {
        // Two items in flight. The first fault starts the shutdown; the second arrives
        // after it and would otherwise overwrite the reason the queue went down. The
        // second processor deliberately ignores its cancellation token, so the shutdown
        // cannot reclassify its fault as Cancelled and swallow it.
        var root = new InvalidOperationException("root");
        var later = new InvalidOperationException("later");

        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var queue = new SaWorkQueue<int>(
            SaWorkQueueOptions<int>.Create(async (int input, CancellationToken _) =>
            {
                if (input == 0)
                {
                    throw root;
                }

                await releaseSecond.Task.ConfigureAwait(false);
                throw later;
            })
                .WithConcurrencyLimit(2)
                .WithQueueCapacity(2)
                .WithShutdownTimeout(TimeSpan.FromSeconds(5))
                .WithHandleItemFaulted((_, ex) =>
                {
                    (ex == root ? rootHandled : laterHandled).TrySetResult();
                    return SaExecutionErrorStrategy.ShutdownQueue;
                }));

        await queue.Enqueue(0, TestToken);
        await queue.Enqueue(1, TestToken);

        await rootHandled.Task.WaitAsync(TestToken);
        await WaitForShutdownErrorAsync(queue, TestToken);

        // Only now does the second item fail — after the root cause is already the
        // recorded reason.
        releaseSecond.SetResult();

        await laterHandled.Task.WaitAsync(TestToken);
        await WaitForShutdownErrorAsync(queue, TestToken);

        Assert.False(queue.IsEnabled);
        Assert.Same(root, queue.ShutdownError);
    }

    /// <summary>
    /// Waits until <see cref="ISaWorkQueue{TInput}.ShutdownError"/> has been written.
    /// </summary>
    /// <remarks>
    /// The write happens just after <c>HandleItemFaulted</c> returns, on the reader's
    /// continuation, so a test that only synchronised on the callback would race it.
    /// </remarks>
    static async Task WaitForShutdownErrorAsync<TInput>(
        ISaWorkQueue<TInput> queue,
        CancellationToken cancellationToken)
    {
        while (queue.ShutdownError is null)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public void AddSaWorkQueue_RejectsNonSingletonLifetime()
    {
        var services = new ServiceCollection();

        // A scoped or transient queue gives every resolution its own pool and its own
        // copy of the buffer. It "works" right up until two scopes both enqueue the
        // same work, so it is rejected at registration instead.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddSaWorkQueue(
                _ => SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask),
                ServiceLifetime.Scoped));

        Assert.Equal("lifetime", ex.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void AddSaWorkQueue_AcceptsSingletonLifetime()
    {
        var services = new ServiceCollection();

        services.AddSaWorkQueue(_ => SaWorkQueueOptions<int>.Create((_, _) => Task.CompletedTask));

        var descriptor = Assert.Single(services);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<ISaWorkQueue<int>>();

        // One resolution, one instance — the whole point of the singleton rule.
        Assert.Same(queue, provider.GetRequiredService<ISaWorkQueue<int>>());
    }
}
