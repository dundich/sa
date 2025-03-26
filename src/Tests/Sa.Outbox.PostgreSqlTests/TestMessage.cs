using Sa.Outbox.Support;

namespace Sa.Outbox.PostgreSqlTests;

[OutboxMessage]

public class TestMessage : IOutboxPayloadMessage
{
    public string Message { get; set; } = default!;
    public string? Content { get; set; }
    public int TenantId { get; set; }
}