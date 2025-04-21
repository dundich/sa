namespace Sa.Outbox.Support;


[OutboxMessage]
public record PingMessage(long Payload) : IOutboxPayloadMessage
{
    public string PayloadId => String.Empty;

    public int TenantId => 0;
}
