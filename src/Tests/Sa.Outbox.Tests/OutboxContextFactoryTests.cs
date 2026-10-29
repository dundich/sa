using Sa.Outbox.Delivery;

namespace Sa.Outbox.Tests;

/// <summary>
/// Pins the pooled context contract: a returned context is rented again and carries nothing from
/// the previous delivery.
/// </summary>
public class OutboxContextFactoryTests
{
    private sealed record PooledMessage(string Value);

    [Fact]
    public void Return_ThenCreate_ReusesTheContextAndResetsIt()
    {
        var factory = new OutboxContextFactory();
        var part = new OutboxPartInfo(1, "part", DateTimeOffset.UnixEpoch);

        IOutboxContextOperations<PooledMessage> first = factory.Create(Message("id-1", new PooledMessage("first"), part));
        first.Ok("done");
        first.Postpone(TimeSpan.FromSeconds(3), "postponed");

        factory.Return(first);

        IOutboxContextOperations<PooledMessage> second = factory.Create(Message("id-2", new PooledMessage("second"), part));

        Assert.Same(first, second);
        Assert.Equal("id-2", second.PayloadId);
        Assert.Equal("second", second.Payload.Value);

        // Nothing from the previous delivery survives the reset.
        Assert.Equal(DeliveryStatusCode.Pending, second.DeliveryResult.Code);
        Assert.Null(second.Exception);
        Assert.Equal(TimeSpan.Zero, second.PostponeDelay);
    }

    private static OutboxDeliveryMessage<PooledMessage> Message(
        string payloadId,
        PooledMessage payload,
        OutboxPartInfo part)
    {
        var message = new OutboxMessage<PooledMessage>(payloadId, payload, part);
        var info = new OutboxTaskDeliveryInfo(
            TaskId: 1,
            DeliveryId: 0,
            Attempt: 0,
            LastErrorId: 0,
            Status: default,
            PartInfo: part);

        return new OutboxDeliveryMessage<PooledMessage>(Guid.NewGuid(), message, info);
    }
}
