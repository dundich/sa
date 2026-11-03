using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests.PlugServices;

/// <summary>
/// The per-provider warm-up gate: runs the provider's initializer once, shares the task across
/// callers, retries after a failure and never poisons the cache when one waiter is cancelled.
/// </summary>
public class OutboxProviderInitializationTests
{
    private const string ProviderKey = "test-provider";

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(IOutboxProviderInitializer? initializer)
    {
        var services = new ServiceCollection();

        if (initializer is not null)
            services.AddKeyedSingleton(ProviderKey, initializer);

        services.AddSingleton<OutboxProviderInitialization>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task NoInitializer_IsACompletedNoOp()
    {
        using ServiceProvider sp = Build(initializer: null);

        await sp.GetRequiredService<OutboxProviderInitialization>()
            .EnsureAsync(ProviderKey, TestToken);
    }

    [Fact]
    public async Task ConcurrentCallers_RunTheInitializerOnce()
    {
        var initializer = new BlockingInitializer();
        using ServiceProvider sp = Build(initializer);

        var gate = sp.GetRequiredService<OutboxProviderInitialization>();

        Task first = gate.EnsureAsync(ProviderKey, TestToken);
        Task second = gate.EnsureAsync(ProviderKey, TestToken);

        initializer.Release();

        await Task.WhenAll(first, second);

        Assert.Equal(1, initializer.Calls);
    }

    [Fact]
    public async Task Failure_IsRetriedOnTheNextCall()
    {
        var initializer = new FailOnceInitializer();
        using ServiceProvider sp = Build(initializer);

        var gate = sp.GetRequiredService<OutboxProviderInitialization>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.EnsureAsync(ProviderKey, TestToken));

        await gate.EnsureAsync(ProviderKey, TestToken);

        Assert.Equal(2, initializer.Calls);
    }

    [Fact]
    public async Task Failure_IsRetried_EvenWhenTheInitializerCompletesInline()
    {
        // Inline Post makes Task.Yield resume synchronously, i.e. *inside* the cache lookup and
        // before the task is inserted. That is the window where a fast fault used to be cached
        // forever; the gate must publish the task before it starts the initializer.
        var initializer = new FailOnceInitializer();
        using ServiceProvider sp = Build(initializer);

        var gate = sp.GetRequiredService<OutboxProviderInitialization>();

        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => gate.EnsureAsync(ProviderKey, TestToken));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await gate.EnsureAsync(ProviderKey, TestToken);

        Assert.Equal(2, initializer.Calls);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotPoisonTheCache()
    {
        var initializer = new BlockingInitializer();
        using ServiceProvider sp = Build(initializer);

        var gate = sp.GetRequiredService<OutboxProviderInitialization>();

        using var cts = new CancellationTokenSource();
        Task first = gate.EnsureAsync(ProviderKey, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The initialization itself keeps running; a later caller joins the same task.
        initializer.Release();
        await gate.EnsureAsync(ProviderKey, TestToken);

        Assert.Equal(1, initializer.Calls);
    }

    private sealed class BlockingInitializer : IOutboxProviderInitializer
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _gate.TrySetResult();

        public async ValueTask InitializeAsync()
        {
            Interlocked.Increment(ref _calls);
            await _gate.Task.ConfigureAwait(false);
        }
    }

    private sealed class FailOnceInitializer : IOutboxProviderInitializer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask InitializeAsync()
        {
            int call = Interlocked.Increment(ref _calls);

            return call == 1
                ? ValueTask.FromException(new InvalidOperationException("warm-up failed"))
                : ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Runs posted continuations on the calling thread, so <c>Task.Yield</c> resumes before the
    /// caller gets control back — the worst-case timing for a cache published after the start.
    /// </summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }
}
