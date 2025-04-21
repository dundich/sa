using Sa.Outbox.Support;

namespace Sa.Outbox.PostgreSqlTests;

[OutboxMessage]

public class TestMessage : IOutboxPayloadMessage
{
    public string PayloadId { get; set; } = default!;
    public string? Content { get; set; }
    public int TenantId { get; set; }
}