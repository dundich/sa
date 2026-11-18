using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.Configuration;
using Sa.Outbox.PostgreSql.Poison;
using Sa.Outbox.Publication;
using System.Collections.Concurrent;
using System.Text;

namespace Sa.Outbox.PostgreSqlTests.Delivery;

/// <summary>Records what the provider hands to the PostgreSQL poison handler.</summary>
public sealed class RecordingPgPoisonHandler : IPgOutboxPoisonHandler
{
    private readonly ConcurrentQueue<PgOutboxPoisonMessage> _messages = new();

    /// <summary>The strategy returned to the provider; <c>Default</c> keeps the built-in <c>__error$</c>.</summary>
    public PgOutboxPoisonAction Result { get; set; } = PgOutboxPoisonAction.Default;

    public PgOutboxPoisonMessage[] Messages => [.. _messages];

    public void Reset()
    {
        _messages.Clear();
        Result = PgOutboxPoisonAction.Default;
    }

    public ValueTask<PgOutboxPoisonAction> HandleAsync(PgOutboxPoisonMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return ValueTask.FromResult(Result);
    }
}

public class DeliveryPermanentErrorTests(DeliveryPermanentErrorTests.Fixture fixture)
    : IClassFixture<DeliveryPermanentErrorTests.Fixture>
{

    public class TestException(string message) : Exception(message)
    {
    }


    class TestMessageConsumer : IConsumer<TestMessage>
    {
        private static readonly TestException s_err = new("test permanent error");
        public async ValueTask ConsumeAsync(
            OutboxConsumerSettings settings,
            OutboxMessageFilter filter,
            ReadOnlyMemory<IOutboxContextOperations<TestMessage>> messages,
            CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            foreach (var msg in messages.Span)
            {
                msg.Fail(s_err, "test");
            }
        }
    }


    public class Fixture : OutboxPostgreSqlFixture<IDeliveryProcessor>
    {
        public Fixture() : base()
        {
            Services.RemoveAll<IPgOutboxPoisonHandler>();
            Services.AddSingleton<IPgOutboxPoisonHandler>(Handler);

            Services
                .AddSaOutbox(builder => builder
                    .WithTenants((_, sp) => sp.WithTenantIds(1, 2))
                    .WithMetadata((_, b) => b.AddMetadata<TestMessage>("permanent", m => m.PayloadId))
                    .WithDeliveries(builder => builder
                        .AddDeliveryScoped<TestMessageConsumer, TestMessage>("test2", (_, s) =>
                        {
                            OutboxSettings = s.WithNoBatchingWindow().Build();
                        })
                )
            );
        }

        public RecordingPgPoisonHandler Handler { get; } = new();

        public OutboxConsumerSettings OutboxSettings = default!;

        public IOutboxMessagePublisher Publisher => ServiceProvider.GetRequiredService<IOutboxMessagePublisher>();
    }

    private readonly PgOutboxTableSettings _tableSettings = new();

    private IDeliveryProcessor Sub => fixture.Sub;


    [Fact]
    public async Task Deliver_ErrorProcess_MustBe_Logged()
    {
        Console.Write(fixture.ConnectionString, TestContext.Current.CancellationToken);

        fixture.Handler.Reset();

        List<TestMessage> messages =
        [
            new TestMessage { PayloadId = "11", Content = "Message 1", TenantId = 1},
            new TestMessage { PayloadId = "12", Content = "Message 2", TenantId = 2}
        ];

        var cnt = await fixture.Publisher.Publish(messages, m => m.TenantId, TestContext.Current.CancellationToken);
        Assert.True(cnt > 0);

        var result = await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, CancellationToken.None);

        // Both messages were handled, even though both ended in a permanent error — the count is
        // handled messages, not successes.
        Assert.Equal(2, result);

        var sql = $"select count(*) from {_tableSettings.Error.TableName}";
        int errCount = await fixture.DataSource.ExecuteReaderFirstAsync<int>(sql, TestContext.Current.CancellationToken);

        // One row, not two: both messages carry the same exception instance, so they de-duplicate.
        Assert.Equal(1, errCount);

        // The pluggable handler is an additional sink: both permanent outcomes reached it before
        // the task rows were finalized, with the payload re-serialized by the provider.
        PgOutboxPoisonMessage[] poisoned = fixture.Handler.Messages;

        Assert.Equal(2, poisoned.Length);
        Assert.Equal(["11", "12"], poisoned.Select(m => m.PayloadId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.All(poisoned, message =>
        {
            Assert.Equal("test2", message.ConsumerGroup);
            Assert.Equal("permanent", message.Part);
            Assert.Equal(DeliveryStatusCode.Error, message.StatusCode);
            Assert.Equal(1, message.Attempt);
            Assert.NotEqual(Guid.Empty, message.OutboxId);
            Assert.True(message.TaskId > 0);
        });

        Assert.Contains(
            "Message 1",
            Encoding.UTF8.GetString(poisoned.Single(m => m.PayloadId == "11").Payload.Span));
        Assert.All(poisoned, message => Assert.Equal("test permanent error", message.Exception.Message));
    }

    [Fact]
    public async Task Handled_Action_SkipsErrorLogAndFinalizesTask()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        fixture.Handler.Reset();
        fixture.Handler.Result = PgOutboxPoisonAction.Handled;

        try
        {
            List<TestMessage> messages =
            [
                new TestMessage { PayloadId = "21", Content = "handled", TenantId = 1 }
            ];

            Assert.True(await fixture.Publisher.Publish(messages, m => m.TenantId, ct) > 0);
            Assert.Equal(1, await Sub.ProcessMessages<TestMessage>(fixture.OutboxSettings, ct));

            PgOutboxPoisonMessage message = Assert.Single(fixture.Handler.Messages);
            Assert.Equal("21", message.PayloadId);

            // The task still becomes terminal (Error), but without an error_id: the handler chose
            // Handled, so the provider skipped __error$.
            Assert.Equal(DeliveryStatusCode.Error, await TaskStatusAsync("21"));
            Assert.Equal(0, await TaskErrorIdAsync("21"));
        }
        finally
        {
            fixture.Handler.Reset();
        }
    }

    private Task<DeliveryStatusCode> TaskStatusAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirstAsync<DeliveryStatusCode>(
            $"""
            SELECT {_tableSettings.TaskQueue.Fields.DeliveryStatusCode}
            FROM {_tableSettings.TaskQueue.TableName}
            WHERE {_tableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, TestContext.Current.CancellationToken);

    // COALESCE because the data source has no Nullable<T> reader support, and 0 unambiguously means
    // "no error id" (ids are sequence-generated and start at 1).
    private Task<long> TaskErrorIdAsync(string payloadId) =>
        fixture.DataSource.ExecuteReaderFirstAsync<long>(
            $"""
            SELECT COALESCE({_tableSettings.TaskQueue.Fields.ErrorId}, 0)
            FROM {_tableSettings.TaskQueue.TableName}
            WHERE {_tableSettings.TaskQueue.Fields.MsgPayloadId} = '{payloadId}'
            """, TestContext.Current.CancellationToken);
}
