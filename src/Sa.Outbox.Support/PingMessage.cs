namespace Sa.Outbox.Support;


[OutboxMessage]
public record PingMessage(string PayloadId, int TenantId = 0) : IOutboxPayloadMessage;
