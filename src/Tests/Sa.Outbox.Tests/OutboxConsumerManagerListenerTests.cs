using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// Listener delivery and transform isolation in <see cref="OutboxConsumerManager"/>.
/// </summary>
public class OutboxConsumerManagerListenerTests
{
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static OutboxConsumerSettings CreateSettings(string consumerGroupId)
        => new(
            ConsumerGroupId: consumerGroupId,
            AsSingleton: false,
            Interval: TimeSpan.FromSeconds(5),
            InitialDelay: TimeSpan.Zero,
            ConcurrencyLimit: 1,
            MaxConcurrency: 1,
            RetryCountOnError: 0,
            MaxBatchSize: 16,
            MaxProcessingIterations: -1,
            IterationDelay: TimeSpan.Zero,
            LockDuration: TimeSpan.FromSeconds(10),
            LockRenewal: TimeSpan.FromSeconds(3),
            LookbackInterval: TimeSpan.FromDays(7),
            MaxDeliveryAttempts: 3,
            BatchingWindow: TimeSpan.Zero,
            PerTenantTimeout: TimeSpan.Zero,
            PerTenantMaxDegreeOfParallelism: 1,
            Paused: false,
            Version: 0);

    [Fact]
    public async Task Apply_TransformCallsBackIntoManager_DoesNotDeadlock()
    {
        var manager = new OutboxConsumerManager();
        const string group = "apply-reentrant";
        manager.TryRegister(group, CreateSettings(group));

        var apply = Task.Run(() => manager.Apply(group, s =>
        {
            // A transform that reads (or pauses) the manager used to hit the same non-reentrant
            // Lock that Apply itself was holding.
            _ = manager.Get(group);
            _ = manager.IsPaused(group);
            return s with { MaxBatchSize = 7 };
        }), TestToken);

        await apply.WaitAsync(TimeSpan.FromSeconds(5), TestToken);
        Assert.Equal(7, manager.Get(group)!.MaxBatchSize);
    }

    [Fact]
    public void Notify_ListenerUnsubscribesItself_DoesNotSkipOtherListeners()
    {
        var manager = new OutboxConsumerManager();
        const string group = "listener-self-dispose";
        int secondCalls = 0;

        IDisposable? selfRemoving = null;
        selfRemoving = manager.Subscribe(group, _ => selfRemoving!.Dispose());
        using var _ = manager.Subscribe(group, _ => Interlocked.Increment(ref secondCalls));

        manager.TryRegister(group, CreateSettings(group));

        // Enumerating the live list threw from the first listener's Dispose and skipped the second.
        Assert.Equal(1, secondCalls);
    }

    [Fact]
    public void Notify_ThrowingListener_DoesNotPreventLaterListeners()
    {
        var manager = new OutboxConsumerManager();
        const string group = "listener-throws";
        int secondCalls = 0;

        using var _ = manager.Subscribe(group, _ => throw new InvalidOperationException("boom"));
        using var __ = manager.Subscribe(group, _ => Interlocked.Increment(ref secondCalls));

        manager.TryRegister(group, CreateSettings(group));

        Assert.Equal(1, secondCalls);
    }
}
