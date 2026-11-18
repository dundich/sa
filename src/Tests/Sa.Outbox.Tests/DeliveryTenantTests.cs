using Sa;
using Sa.Outbox.Delivery;
using Sa.Outbox.Metadata;
using Sa.Outbox.PlugServices;

namespace Sa.Outbox.Tests;

/// <summary>
/// The lock renewer is the only thing standing between a slow batch and a second worker renting the
/// same rows. <see cref="LockRenewerHandle"/> fails only when the extension delegate throws, so a
/// provider that answers "nothing was renewed" used to be indistinguishable from a healthy renewal:
/// the batch kept running under a lease somebody else already owned.
/// <para>
/// The renewer is the only part of this pipeline that speaks to the storage in the background, so
/// these tests drive <see cref="DeliveryTenant"/> directly with fakes rather than through a
/// container — no database is involved and no timing window has to be won.
/// </para>
/// </summary>
public sealed class DeliveryTenantTests
{
    private sealed class TestMessage { }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static OutboxConsumerSettings CreateSettings(
        TimeSpan? lockRenewal = null,
        TimeSpan? lockDuration = null)
        => new OutboxConsumerSettings
        {
            ConsumerGroupId = "test-group",
            AsSingleton = false,
            Interval = TimeSpan.FromMinutes(1),
            InitialDelay = TimeSpan.Zero,
            ConcurrencyLimit = 1,
            MaxConcurrency = 1,
            RetryCountOnError = 0,
            MaxBatchSize = 16,
            MaxProcessingIterations = 1,
            IterationDelay = TimeSpan.Zero,
            LockDuration = lockDuration ?? TimeSpan.FromSeconds(10),
            LockRenewal = lockRenewal ?? TimeSpan.FromMilliseconds(20),
            LookbackInterval = TimeSpan.FromDays(7),
            MaxDeliveryAttempts = 3,
            BatchingWindow = TimeSpan.Zero,
            PerTenantTimeout = TimeSpan.Zero,
            PerTenantMaxDegreeOfParallelism = 1,
            Paused = false,
            Version = 0
        };

    private static DeliveryTenant CreateTenant(FakeManager manager, IDeliveryCourier courier)
        => new(
            new FakeStorage(manager),
            courier,
            new FakeBatcher(),
            new FilterFactory(new FakeMetadataProvider()),
            new FakeContextFactory());

    /// <summary>
    /// A renewal that reached no row is the provider's way of saying the lease is gone. It must be
    /// reported as a lost lock, and only after the batch has been released — the statuses are the
    /// work, the lock is not.
    /// </summary>
    [Fact]
    public async Task RenewalThatRenewsNothing_IsReportedAsALostLock()
    {
        var manager = new FakeManager(_ => Task.FromResult(0));
        var courier = new FakeCourier(async ct => await Task.Delay(200, ct));
        var tenant = CreateTenant(manager, courier);

        await Assert.ThrowsAsync<LockRenewalException>(
            () => tenant.ProcessInTenant<TestMessage>(1, CreateSettings(), TestToken));

        Assert.True(manager.Returns > 0, "the batch must be released before the lost lock surfaces");
        Assert.True(manager.Renewals > 0, "the renewer must have run at least once");
    }

    /// <summary>
    /// The counter-test: a provider that renews the batch keeps it alive for as long as the consumer
    /// needs, and the pass returns the delivered count.
    /// </summary>
    [Fact]
    public async Task RenewalThatRenewsTheBatch_KeepsThePassAlive()
    {
        var manager = new FakeManager(_ => Task.FromResult(1));
        var courier = new FakeCourier(async ct => await Task.Delay(120, ct));
        var tenant = CreateTenant(manager, courier);

        int delivered = await tenant.ProcessInTenant<TestMessage>(1, CreateSettings(), TestToken);

        Assert.Equal(1, delivered);
        Assert.True(manager.Renewals > 0, "a slow batch must actually be renewed");
    }

    /// <summary>
    /// The renewer keeps ticking while the statuses are written, and by then the rows are no longer
    /// <c>Processing</c> — a zero in that window must not abort a batch that has already been
    /// delivered. This is the race the check above would otherwise introduce: against PostgreSQL a
    /// 20 ms renewal against a 50 ms lease lands right after <c>ReturnDelivery</c> often enough to
    /// turn a delivered batch into an "empty" pass.
    /// </summary>
    [Fact]
    public async Task RenewalDuringRelease_IsNotALostLock()
    {
        var manager = new FakeManager(_ => Task.FromResult(0))
        {
            ReturnDelay = TimeSpan.FromMilliseconds(100),
        };
        var courier = new FakeCourier(_ => Task.CompletedTask);
        var tenant = CreateTenant(manager, courier);

        int delivered = await tenant.ProcessInTenant<TestMessage>(1, CreateSettings(), TestToken);

        Assert.Equal(1, delivered);
        Assert.True(manager.Renewals > 0, "the renewer must have ticked during the release");
    }

    private sealed class FakeManager(Func<CancellationToken, Task<int>> extendDelivery)
        : IOutboxDeliveryManager
    {
        /// <summary>How long <see cref="ReturnDelivery"/> takes, so the renewer keeps ticking during it.</summary>
        public TimeSpan ReturnDelay { get; init; }

        public int Renewals { get; private set; }

        public int Returns { get; private set; }

        public Task<int> RentDelivery<TMessage>(
            Memory<IOutboxContextOperations<TMessage>> writeBuffer,
            TimeSpan lockDuration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
        {
            writeBuffer.Span[0] = new FakeOutboxContext<TMessage>(payloadId: "msg-1");
            return Task.FromResult(1);
        }

        public async Task<int> ReturnDelivery<TMessage>(
            ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
        {
            if (ReturnDelay > TimeSpan.Zero)
                await Task.Delay(ReturnDelay, cancellationToken);

            Returns += messages.Length;
            return messages.Length;
        }

        public Task<int> ExtendDeliveryAsync(
            TimeSpan lockExpiration,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
        {
            Renewals++;
            return extendDelivery(cancellationToken);
        }
    }

    private sealed class FakeCourier(Func<CancellationToken, Task> deliver) : IDeliveryCourier
    {
        public async ValueTask<int> Deliver<TMessage>(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TMessage>> messages,
            CancellationToken cancellationToken)
        {
            await deliver(cancellationToken);
            return messages.Length;
        }
    }

    private sealed class FakeBatcher : IDeliveryBatcher
    {
        public ValueTask<int> CalculateBatchSizeAsync(
            int maxBatchSize,
            OutboxMessageFilter filter,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(maxBatchSize);
    }

    private sealed class FakeMetadataProvider : IOutboxMessageMetadataProvider
    {
        public OutboxMessageMetadata GetMetadata(Type messageType) => OutboxMessageMetadata.Empty;
    }

    private sealed class FakeContextFactory : IOutboxContextFactory
    {
        public IOutboxContextOperations<TMessage> Create<TMessage>(OutboxDeliveryMessage<TMessage> deliveryMessage)
            => new FakeOutboxContext<TMessage>(deliveryMessage.PayloadId);
    }

    private sealed class FakeStorage(IOutboxDeliveryManager manager) : IOutboxStorage
    {
        public IOutboxBulkWriter Writer(string providerKey) => throw new NotSupportedException();

        public IOutboxDeliveryManager DeliveryManager(string providerKey) => manager;

        public IOutboxTenantDetector? TenantDetector(string providerKey) => null;

        public Sa.Outbox.Partitional.TenantSettings TenantSettings(string providerKey)
            => throw new NotSupportedException();
    }
}
