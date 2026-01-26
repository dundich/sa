namespace Sa.Outbox.PostgreSqlTests;


internal sealed class TestMessage
{
    public required string PayloadId { get; set; }
    public string? Content { get; set; }
    public int TenantId { get; set; }
}
