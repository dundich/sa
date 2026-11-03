using Microsoft.Extensions.DependencyInjection;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.Publication;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>
/// A cancelled delivery pass must not lose the batch and must not spend its retry budget: the
/// rows stay rented (Processing) until the lock expires, the next pass of the same group re-rents
/// them, and <c>delivery_attempt</c> is only incremented by the finish that actually writes a
/// status. Shutdown, then, costs latency — never a message and never an attempt.
/// </summary>
public class DeliveryCancellationRetryTests(DeliveryCancellationRetryTests.Fixture fixture)
    : IClassFixture<DeliveryCancellationRetryTests.Fixture>
{
    private const int TenantId = 1;

    private sealed class CancelOnceConsumer : IConsumer<TestMessage>
    {
        /// <summary>Set by the test: while non-null, the first call cancels it and throws.</summary>
        public static CancellationTokenSource? Cts;

        public ValueTask Consume(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            if (Cts is { } cts)
            {
                Cts = null;
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            return ValueTask.CompletedTask;
        }
    }

    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public OutboxConsumerSettings Settings { get; private set; } = default!;

        public Fixture() : base()
        {
            Services.AddSaOutbox(builder => builder
                .WithMetadata((_, metadata) => metadata.AddMetadata<TestMessage>("root_1", m => m.PayloadId))
                .WithTenants((_, tenants) => tenants.WithTenantIds(TenantId))
                .WithDeliveries(deliveries => deliveries
                    .AddDeliveryScoped<CancelOnceConsumer, TestMessage>("cancel-retry", (_, settings) =>
                    {
                        settings
                            .WithMaxBatchSize(1)
                            .WithNoLockDuration()
                            .WithLockRenewal(TimeSpan.FromMilliseconds(10))
                            .WithNoBatchingWindow();

                        Settings = settings.Build();
                    })));
        }

        public IOutboxMessagePublisher Publisher =>
            ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();

        public IDeliveryProcessor Processor => Sub;
    }

    [Fact]
    public async Task CancelledPass_LosesNothing_AndDoesNotSpendAnAttempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        ulong published = await fixture.Publisher.Publish(
            [new TestMessage { PayloadId = "cancel-1", Content = "cancel-1", TenantId = TenantId }],
            TenantId,
            ct);

        Assert.Equal(1UL, published);

        using var cts = new CancellationTokenSource();
        CancelOnceConsumer.Cts = cts;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Processor.ProcessMessages<TestMessage>(fixture.Settings, cts.Token));

        // Nothing was acknowledged — not even as a failure — so the row is still owned by the pass
        // that stopped and its attempt counter has not moved.
        (int attempt, int status) cancelled = await ReadTaskRow();
        Assert.Equal(0, cancelled.attempt);
        Assert.Equal((int)DeliveryStatusCode.Processing, cancelled.status);

        // Re-rent is gated on `lock_expires_on < now`, and those are whole unix seconds — one
        // second boundary has to pass before the next poll may take the row back.
        await Task.Delay(1500, ct);

        long processed = await fixture.Processor.ProcessMessages<TestMessage>(fixture.Settings, ct);

        Assert.Equal(1, processed);

        // Attempt 1 comes from the finish that actually delivered it, not from the cancelled pass.
        (int attempt, int status) delivered = await ReadTaskRow();
        Assert.Equal(1, delivered.attempt);
        Assert.Equal((int)DeliveryStatusCode.Ok, delivered.status);
    }

    private async Task<(int Attempt, int Status)> ReadTaskRow()
    {
        var settings = new PgOutboxTableSettings();
        string table = settings.TaskQueue.TableName;
        string attempt = settings.TaskQueue.Fields.DeliveryAttempt;
        string status = settings.TaskQueue.Fields.DeliveryStatusCode;
        string tenant = settings.TaskQueue.Fields.TenantId;

        CancellationToken ct = TestContext.Current.CancellationToken;

        int attemptValue = await fixture.DataSource.ExecuteReaderFirst<int>(
            $"select {attempt} from {table} where {tenant} = {TenantId}", ct);

        int statusValue = await fixture.DataSource.ExecuteReaderFirst<int>(
            $"select {status} from {table} where {tenant} = {TenantId}", ct);

        return (attemptValue, statusValue);
    }
}
